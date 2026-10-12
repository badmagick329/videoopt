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

    public async Task<ImmichDiscoveryResult> DiscoverAsync(string databasePath, AppSettings settings, bool first, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = clients.Create(settings.Immich);
        var cutoff = _utcNow().AddDays(-settings.Immich.MinimumAgeDays);
        var rulesFingerprint = EligibilityEvaluator.RulesFingerprint(settings.Eligibility);
        var minimumSize = SmallestMinimumFileSize(settings.Eligibility);
        var history = await ImmichHistory.LoadAsync(jobs, databasePath, cancellationToken);
        var skips = await jobs.ListImmichSkipsAsync(databasePath, cancellationToken);

        var queued = new List<JobRecord>();
        int alreadyQueued = 0, alreadyConverted = 0, previouslyFailed = 0, previouslySkipped = 0, excluded = 0, ineligible = 0, skipped = 0, issues = 0;
        // Paging can return an asset twice if the library changes between pages; judge each one once.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var asset in client.SearchVideosAsync(cutoff, includeFavorites: !settings.Immich.SkipFavorites, cancellationToken))
        {
            // --first means one video in flight: a rerun after a failed start resumes that video rather than adding a second.
            if (first && queued.Count + alreadyQueued > 0) break;
            if (!seen.Add(asset.Id)) continue;

            if (Exclusion(asset, cutoff, settings.Immich.SkipFavorites) is { } exclusion)
            {
                excluded++;
                progress?.Report(new ScanProgress(asset.OriginalFileName, "Excluded", $"Immich returned asset {asset.Id}, which {exclusion}; leaving it alone."));
                continue;
            }

            if (history.OpenAssets.Contains(asset.Id)) { alreadyQueued++; continue; }
            if (history.IsConverted(asset)) { alreadyConverted++; continue; }
            if (history.IsSettledFailure(asset)) { previouslyFailed++; continue; }
            if (skips.TryGetValue(asset.Id, out var skip) && skip.IsCurrent(asset.Checksum, rulesFingerprint)) { previouslySkipped++; continue; }

            if (!settings.Eligibility.Extensions.Contains(Path.GetExtension(asset.OriginalFileName), StringComparer.OrdinalIgnoreCase) ||
                asset.Exif.FileSizeInBytes < minimumSize)
            {
                ineligible++;
                continue;
            }

            if (StagedPath(settings.Immich.StagingDirectory, asset) is not { } stagedPath)
            {
                issues++;
                progress?.Report(new ScanProgress(asset.OriginalFileName, "Issue", $"Asset {asset.Id} has an ID or file name that cannot be staged safely."));
                continue;
            }

            // Only a queued job keeps its staged copy. Every other outcome, including cancellation and errors that end the
            // run, removes it, so staging never holds a file nothing will read.
            var keepStaged = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                progress?.Report(new ScanProgress(stagedPath, "Downloading", $"Downloading {asset.OriginalFileName} from Immich."));
                await client.DownloadOriginalAsync(asset.Id, stagedPath, cancellationToken: cancellationToken);

                // The checksum is the job's identity and what the re-upload guard matches on, so it must describe these bytes.
                if (!string.Equals(await Sha1Base64Async(stagedPath, cancellationToken), asset.Checksum, StringComparison.Ordinal))
                {
                    issues++;
                    progress?.Report(new ScanProgress(stagedPath, "Issue", $"The download of asset {asset.Id} does not match its Immich checksum."));
                    continue;
                }

                progress?.Report(new ScanProgress(stagedPath, "Probing", "Running ffprobe."));
                var media = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(stagedPath, cancellationToken);
                if (!EligibilityEvaluator.IsEligible(new FileInfo(stagedPath), media, settings.Eligibility, out var reason))
                {
                    await jobs.PutImmichSkipAsync(databasePath, new ImmichSkipRecord(asset.Id, asset.Checksum, rulesFingerprint, reason, _utcNow()), cancellationToken);
                    skipped++;
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
            }
            // Failures of this one asset: Immich refused the request, or ffprobe could not read the file. Anything else
            // (Immich unreachable, a rejected key, a missing ffprobe, a full or unwritable staging disk) would fail every
            // asset the same way after a full download, so it ends the run.
            catch (Exception exception) when (exception is ImmichApiException { Code: "ImmichRequestFailed" } or InvalidDataException)
            {
                issues++;
                progress?.Report(new ScanProgress(stagedPath, "Issue", exception.Message));
            }
            finally
            {
                if (!keepStaged) DeleteStaged(stagedPath);
            }
        }

        return new ImmichDiscoveryResult(queued, alreadyQueued, alreadyConverted, previouslyFailed, previouslySkipped, excluded, ineligible, skipped, issues);
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
