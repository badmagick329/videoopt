using System.Security.Cryptography;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Infrastructure.Jobs;

namespace VideoOptimiser.Infrastructure.Immich;

/// <summary>
/// Finds old Immich videos worth converting, stages each one locally and queues it as an Immich job. Eligibility needs a
/// probe of the real file, so every candidate that survives the free checks is downloaded once; an ineligible one is
/// recorded as an Immich skip so later runs leave it alone until its checksum or the eligibility rules change.
/// </summary>
public sealed class ImmichDiscoveryService(
    IImmichClientFactory clients,
    IJobRepository jobs,
    Func<string, IMediaProbe> mediaProbeFactory,
    IFileFingerprintService fingerprints,
    Func<DateTimeOffset>? utcNow = null) : IImmichDiscoveryService
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public async Task<ImmichDiscoveryResult> DiscoverAsync(string databasePath, AppSettings settings, int? limit, IProgress<ImmichDiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = clients.Create(settings.Immich);
        var cutoff = _utcNow().AddDays(-settings.Immich.MinimumAgeDays);
        var rulesFingerprint = EligibilityEvaluator.RulesFingerprint(settings.Eligibility);
        var qualityFingerprint = QualityFingerprint.Compute(settings);
        var minimumSize = SmallestMinimumFileSize(settings.Eligibility);
        // Immich gives no bitrate, so this uses the file size; a video whose stream is too small is skipped by its job before
        // the search instead.
        if (!Domain.HumanReadableValues.TryParseSize(settings.Savings.MinimumBytesSaved, out var minimumBytesSaved))
            throw new InvalidOperationException("savings.minimumBytesSaved must be validated before Immich discovery.");
        // Before the history load: a job this fails has ended, so the search below may stage its asset afresh.
        var (restaged, issues) = await RestageMissingAsync(client, databasePath, settings, limit, progress, cancellationToken);
        var history = await ImmichHistory.LoadAsync(jobs, databasePath, cancellationToken);
        var skips = await jobs.ListImmichSkipsAsync(databasePath, cancellationToken);
        // Said up front because each of these is downloaded again: the user would otherwise see a long run of downloads
        // for videos an earlier run already judged.
        var staleSkips = skips.Values.Count(skip => !string.Equals(skip.RulesFingerprint, rulesFingerprint, StringComparison.Ordinal));
        progress?.Report(new ImmichDiscoveryProgress.Started(limit, history.OpenAssets.Count, staleSkips, settings.Eligibility.Rules.Count));

        var queued = new List<JobRecord>();
        int alreadyQueued = 0, alreadyConverted = 0, previouslyFailed = 0, previouslySkipped = 0, excluded = 0, ineligible = 0, skipped = 0, candidates = 0;
        var skipReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        // Paging can return an asset twice if the library changes between pages; judge each one once.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var asset in client.SearchVideosAsync(cutoff, includeFavorites: !settings.Immich.SkipFavorites, cancellationToken))
        {
            // A limit counts videos in flight, open ones included: a rerun after a failed start resumes those videos and adds
            // only enough to reach the limit. The search is oldest first, so open jobs' assets come before new ones.
            if (queued.Count + alreadyQueued >= limit) break;
            if (!seen.Add(asset.Id)) continue;

            if (Exclusion(asset, cutoff, settings.Immich.SkipFavorites) is { } exclusion)
            {
                excluded++;
                progress?.Report(new ImmichDiscoveryProgress.AssetExcluded(asset.OriginalFileName, asset.Id, exclusion));
                continue;
            }

            if (history.OpenAssets.Contains(asset.Id)) { alreadyQueued++; continue; }
            if (history.IsConverted(asset)) { alreadyConverted++; continue; }
            if (history.IsSettledSkip(asset, qualityFingerprint)) { previouslySkipped++; continue; }
            if (history.IsSettledFailure(asset)) { previouslyFailed++; continue; }
            if (skips.TryGetValue(asset.Id, out var skip) && skip.IsCurrent(asset.Checksum, rulesFingerprint)) { previouslySkipped++; continue; }

            if (!settings.Eligibility.Extensions.Contains(Path.GetExtension(asset.OriginalFileName), StringComparer.OrdinalIgnoreCase) ||
                asset.Exif.FileSizeInBytes < minimumSize || asset.Exif.FileSizeInBytes <= minimumBytesSaved)
            {
                ineligible++;
                // A stale skip this check settles without a download would otherwise stay stale for good, and every later run
                // would announce it as one to check again.
                if (skip is not null)
                    await jobs.PutImmichSkipAsync(databasePath, new ImmichSkipRecord(asset.Id, asset.Checksum, rulesFingerprint, "too small or not a listed file type", _utcNow()), cancellationToken);
                continue;
            }

            if (StagedPath(settings.Immich.StagingDirectory, asset) is not { } stagedPath)
            {
                issues++;
                progress?.Report(new ImmichDiscoveryProgress.AssetIssue(asset.OriginalFileName, $"asset {asset.Id} has an ID or file name that cannot be staged safely; it is left alone"));
                continue;
            }

            // Only a queued job keeps its staged copy. Every other outcome, including cancellation and errors that end the
            // run, removes it, so staging never holds a file nothing will read.
            var keepStaged = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                progress?.Report(new ImmichDiscoveryProgress.CandidateDownloading(++candidates, asset.OriginalFileName, asset.LocalDateTime.DateTime));
                await client.DownloadOriginalAsync(asset.Id, stagedPath, cancellationToken: cancellationToken);

                // The checksum is the job's identity and what the re-upload guard matches on, so it must describe these bytes.
                if (!string.Equals(await Sha1Base64Async(stagedPath, cancellationToken), asset.Checksum, StringComparison.Ordinal))
                {
                    issues++;
                    progress?.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Issue, "the download does not match Immich's checksum", RetryNextRun, 0));
                    continue;
                }

                var media = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(stagedPath, cancellationToken);
                // The size of the bytes just probed, which is what the rules were judged on.
                var downloadedBytes = new FileInfo(stagedPath).Length;
                var facts = new CandidateFacts(media.PrimaryVideoCodec, media.PrimaryVideoWidth, media.PrimaryVideoHeight, media.PrimaryVideoBitrate, downloadedBytes);
                if (EligibilityEvaluator.MatchingRule(downloadedBytes, media, settings.Eligibility) is not { } rule)
                {
                    var reason = EligibilityEvaluator.DescribeMismatch(downloadedBytes, media, settings.Eligibility);
                    await jobs.PutImmichSkipAsync(databasePath, new ImmichSkipRecord(asset.Id, asset.Checksum, rulesFingerprint, reason, _utcNow()), cancellationToken);
                    skipped++;
                    skipReasons[reason] = skipReasons.GetValueOrDefault(reason) + 1;
                    progress?.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Skipped, reason, null, 0, facts));
                    continue;
                }

                var job = new JobRecord
                {
                    Id = Guid.NewGuid(),
                    SourcePath = stagedPath,
                    SourceFingerprint = await fingerprints.CreateAsync(stagedPath, cancellationToken),
                    Status = JobStatus.Queued,
                    SourceKind = JobSourceKind.Immich,
                    ImmichAssetId = asset.Id,
                    ImmichChecksum = asset.Checksum
                };
                JobCompatibility.Apply(job, JobCompatibility.ExpectedBinding(job.Id, settings));
                queued.Add(await jobs.CreateAsync(databasePath, job, cancellationToken));
                keepStaged = true;
                progress?.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, queued.Count + alreadyQueued, facts, rule));
            }
            // Failures of this one asset: Immich refused the request, or ffprobe could not read the file. Anything else
            // (Immich unreachable, a rejected key, a missing ffprobe, a full or unwritable staging disk) would fail every
            // asset the same way after a full download, so it ends the run.
            catch (Exception exception) when (exception is ImmichApiException { Code: "ImmichRequestFailed" } or MediaProbeException)
            {
                issues++;
                progress?.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Issue, exception.Message.TrimEnd('.'), RetryNextRun, 0));
            }
            finally
            {
                if (!keepStaged) DeleteStaged(stagedPath);
            }
        }

        return new ImmichDiscoveryResult(queued, restaged, alreadyQueued, alreadyConverted, previouslyFailed, previouslySkipped, excluded, ineligible, skipped, issues,
            [.. skipReasons.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new SkipReasonCount(pair.Key, pair.Value))]);
    }

    // An issue is not recorded as a skip, so the next run downloads the video again.
    private const string RetryNextRun = "it will be tried again next run";

    /// <summary>
    /// Downloads again the staged original of each open job that lost it: a job <c>queue retry</c> created after
    /// <c>cleanup</c> deleted the copy, or one whose copy was deleted by hand. Such a job could never run, and discovery
    /// skips an asset with an open job, so nothing else would bring the file back. The download must match the job's
    /// checksum, so the job converts the bytes it was created for. When Immich no longer returns them, the job fails as
    /// <c>SourceMissing</c>, which leaves the asset free for discovery. With a <paramref name="limit"/>, only that many of
    /// the oldest open jobs count, as they are the ones the run processes.
    /// </summary>
    private async Task<(int Restaged, int Issues)> RestageMissingAsync(IImmichClient client, string databasePath, AppSettings settings, int? limit, IProgress<ImmichDiscoveryProgress>? progress, CancellationToken cancellationToken)
    {
        int restaged = 0, issues = 0;
        // Ready jobs no longer read the original, and a job interrupted during finalisation needs it left as it is.
        var candidates = (await jobs.ListAsync(databasePath, terminal: false, cancellationToken))
            .Where(job => job.SourceKind == JobSourceKind.Immich && job.Status is not (JobStatus.ReadyToFinalize or JobStatus.Finalizing) && job.ResumeStatus != JobStatus.Finalizing)
            .OrderBy(job => job.CreatedUtc)
            .Take(limit ?? int.MaxValue);
        foreach (var job in candidates)
        {
            if (File.Exists(job.SourcePath)) continue;
            if (job.ImmichChecksum is null || !ImmichStaging.IsStagedOriginal(settings.Immich.StagingDirectory, job.ImmichAssetId, job.SourcePath))
            {
                issues++;
                await FailMissingAsync(databasePath, job, "The staged original is missing, and its path is not in the asset's folder under immich.stagingDirectory, so it is not downloaded again.", cancellationToken);
                progress?.Report(new ImmichDiscoveryProgress.AssetIssue(Path.GetFileName(job.SourcePath), $"job {job.Id:N} is open but its staged copy is missing and cannot be downloaded again; the job is marked failed"));
                continue;
            }

            var path = Path.GetFullPath(job.SourcePath);
            var keep = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                progress?.Report(new ImmichDiscoveryProgress.Restaging(Path.GetFileName(path), job.Id));
                await client.DownloadOriginalAsync(job.ImmichAssetId!, path, cancellationToken: cancellationToken);
                if (!string.Equals(await Sha1Base64Async(path, cancellationToken), job.ImmichChecksum, StringComparison.Ordinal))
                {
                    issues++;
                    await FailMissingAsync(databasePath, job, $"The staged original is missing, and Immich asset {job.ImmichAssetId} no longer matches the checksum this job was created for.", cancellationToken);
                    progress?.Report(new ImmichDiscoveryProgress.Restaged(Path.GetFileName(path), "the download no longer matches the checksum this job was created for; the job is marked failed"));
                    continue;
                }

                keep = true;
                restaged++;
                progress?.Report(new ImmichDiscoveryProgress.Restaged(Path.GetFileName(path), null));
            }
            catch (ImmichApiException exception) when (exception.Code == "ImmichRequestFailed")
            {
                issues++;
                await FailMissingAsync(databasePath, job, $"The staged original is missing, and Immich did not return asset {job.ImmichAssetId} again: {exception.Message}", cancellationToken);
                progress?.Report(new ImmichDiscoveryProgress.Restaged(Path.GetFileName(path), $"Immich did not return the video again ({exception.Message.TrimEnd('.')}); the job is marked failed"));
            }
            finally
            {
                if (!keep) DeleteStaged(path);
            }
        }

        return (restaged, issues);
    }

    private async Task FailMissingAsync(string databasePath, JobRecord job, string message, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Failed;
        job.ResumeStatus = null;
        job.FailureCategory = QueueService.SourceMissingCategory;
        job.FailureMessage = message;
        job.CompletedUtc = _utcNow();
        await jobs.UpdateAsync(databasePath, job, cancellationToken);
    }

    /// <summary>
    /// Why an asset the search returned must not be converted, or null for a candidate. This repeats the search filter on
    /// purpose: a wrong match ends with an original in Immich's trash, so the server's filter is not trusted alone.
    /// </summary>
    private static string? Exclusion(ImmichAsset asset, DateTimeOffset cutoff, bool skipFavorites)
    {
        if (!string.Equals(asset.Type, ImmichValues.Video, StringComparison.Ordinal)) return $"is a {asset.Type}, not a video";
        if (asset.FileCreatedAt >= cutoff) return "is newer than the age gate";
        if (skipFavorites && asset.IsFavorite) return "is a favourite";
        if (asset.IsTrashed) return "is in the trash";
        if (asset.Visibility is not (ImmichValues.Timeline or ImmichValues.Archive)) return $"has visibility {asset.Visibility}";
        if (asset.LibraryId is not null) return "belongs to an external library";
        return null;
    }

    // Rules with an unreadable size are rejected by validation; treating them as zero only means downloading more.
    private static long SmallestMinimumFileSize(EligibilitySettings settings) => settings.Rules.Count == 0
        ? long.MaxValue
        : settings.Rules.Min(rule => Domain.HumanReadableValues.TryParseSize(rule.MinimumFileSize, out var size) ? size : 0);

    /// <summary>
    /// <c>&lt;stagingDirectory&gt;/&lt;assetId&gt;/&lt;originalFileName&gt;</c>, or null when either part comes back from
    /// Immich as something other than a plain name, which could put the file outside its folder.
    /// </summary>
    private static string? StagedPath(string stagingDirectory, ImmichAsset asset)
    {
        var fileName = asset.OriginalFileName;
        if (!Guid.TryParseExact(asset.Id, "D", out var id) ||
            !string.Equals(asset.Id, id.ToString("D"), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(fileName) ||
            fileName != Path.GetFileName(fileName) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        // Windows rewrites some names while resolving them (trailing dots and spaces, "." and ".."); accept only a path
        // that still sits directly in the asset's folder under the original name.
        var folder = Path.GetFullPath(Path.Combine(stagingDirectory, asset.Id));
        var path = Path.Combine(folder, fileName);
        return string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal) ? path : null;
    }

    // SHA-1 because that is Immich's checksum; it identifies the file, it does not protect anything.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "Matches Immich's SHA-1 asset checksum; not used for security.")]
    private static async Task<string> Sha1Base64Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToBase64String(await SHA1.HashDataAsync(stream, cancellationToken));
    }

    private static void DeleteStaged(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Still open elsewhere; the next download of this asset overwrites it.
        }

        try
        {
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder still holds other files, or was never created.
        }
    }

    /// <summary>Earlier Immich jobs, read once per run because every repository call re-runs the database initialiser.</summary>
    private sealed class ImmichHistory
    {
        public HashSet<string> OpenAssets { get; } = new(StringComparer.Ordinal);
        private readonly HashSet<string> _convertedAssets = new(StringComparer.Ordinal);
        private readonly HashSet<string> _convertedChecksums = new(StringComparer.Ordinal);
        private readonly HashSet<string> _replacements = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JobRecord> _latestTerminal = new(StringComparer.Ordinal);

        public static async Task<ImmichHistory> LoadAsync(IJobRepository jobs, string databasePath, CancellationToken cancellationToken)
        {
            var history = new ImmichHistory();
            foreach (var job in await jobs.ListAsync(databasePath, terminal: false, cancellationToken))
            {
                if (job.ImmichAssetId is { } assetId) history.OpenAssets.Add(assetId);
                if (job.ImmichNewAssetId is { } replacement) history._replacements.Add(replacement);
            }

            foreach (var job in await jobs.ListAsync(databasePath, terminal: true, cancellationToken))
            {
                if (job.ImmichNewAssetId is { } replacement) history._replacements.Add(replacement);
                if (job.ImmichAssetId is not { } assetId) continue;
                if (job.Status == JobStatus.Completed)
                {
                    history._convertedAssets.Add(assetId);
                    if (job.ImmichChecksum is { } checksum) history._convertedChecksums.Add(checksum);
                }

                if (!history._latestTerminal.TryGetValue(assetId, out var latest) || job.CreatedUtc > latest.CreatedUtc) history._latestTerminal[assetId] = job;
            }

            return history;
        }

        /// <summary>
        /// A completed job converted this asset, or one with the same checksum: an original the phone uploaded again after
        /// conversion, which the re-upload guard trashes. Or it is an AV1 asset that finalisation uploaded, which is just as
        /// old and on the timeline. Discovery must never download or convert any of these.
        /// </summary>
        public bool IsConverted(ImmichAsset asset) =>
            _convertedAssets.Contains(asset.Id) || _convertedChecksums.Contains(asset.Checksum) || _replacements.Contains(asset.Id);

        /// <summary>
        /// The asset's latest job was skipped as not worth converting, under the quality settings in force now. Changing them
        /// makes the asset a candidate again; only then could AV1 pass.
        /// </summary>
        public bool IsSettledSkip(ImmichAsset asset, string qualityFingerprint) =>
            _latestTerminal.TryGetValue(asset.Id, out var job) &&
            job.Status == JobStatus.Skipped &&
            string.Equals(job.ImmichChecksum, asset.Checksum, StringComparison.Ordinal) &&
            string.Equals(job.QualityFingerprint, qualityFingerprint, StringComparison.Ordinal);

        /// <summary>
        /// The asset's latest job failed or was cancelled while the asset was unchanged, so converting it again would repeat
        /// that outcome; only <c>queue retry</c> requeues it, as for a settled original.
        /// </summary>
        public bool IsSettledFailure(ImmichAsset asset) =>
            _latestTerminal.TryGetValue(asset.Id, out var job) &&
            job.Status is JobStatus.Failed or JobStatus.Cancelled &&
            string.Equals(job.ImmichChecksum, asset.Checksum, StringComparison.Ordinal) &&
            !QueueService.RequeuedFailureCategories.Contains(job.FailureCategory ?? string.Empty);
    }
}
