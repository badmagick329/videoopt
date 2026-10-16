using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.ImmichRun;

/// <summary>
/// Finds old Immich videos worth converting, stages each one locally and queues it as an Immich job. Eligibility needs a
/// probe of the real file, so every candidate that survives the free checks is downloaded once; an ineligible one is
/// recorded as an Immich skip so later runs leave it alone until its checksum or the eligibility rules change. It does not
/// download again a queued video's missing copy: <c>run</c> does that for the videos it is about to convert.
/// </summary>
public sealed class ImmichDiscoveryService(
    IImmichClientFactory clients,
    IJobRepository jobs,
    Func<string, IMediaProbe> mediaProbeFactory,
    IFileFingerprintService fingerprints,
    Func<DateTimeOffset>? utcNow = null) : IImmichDiscoveryService
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public async Task<ImmichDiscoveryResult> DiscoverAsync(string databasePath, AppSettings settings, int? limit, IProgress<ImmichProgress>? progress = null, CancellationToken cancellationToken = default)
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
        var history = await ImmichHistory.LoadAsync(jobs, databasePath, cancellationToken);
        var skips = await jobs.ListImmichSkipsAsync(databasePath, cancellationToken);
        // Said up front because each of these is downloaded again: the user would otherwise see a long run of downloads
        // for videos an earlier run already judged.
        var staleSkips = skips.Values.Count(skip => !string.Equals(skip.RulesFingerprint, rulesFingerprint, StringComparison.Ordinal));
        progress?.Report(new ImmichDiscoveryProgress.Started(limit, staleSkips, settings.Eligibility.Rules.Count));

        var queued = new List<JobRecord>();
        // Every open job counts here, whether or not the search reaches its asset; only a job queued from another terminal
        // meanwhile is added below.
        var alreadyQueued = history.Waiting;
        int alreadyConverted = 0, previouslyFailed = 0, previouslySkipped = 0, previouslyRejected = 0, excluded = 0, ineligible = 0, skipped = 0, issues = 0, candidates = 0;
        var skipReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        // Paging can return an asset twice if the library changes between pages; judge each one once.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var asset in client.SearchVideosAsync(cutoff, includeFavorites: !settings.Immich.SkipFavorites, cancellationToken))
        {
            // A limit counts the new videos this search queues, not the open ones: a rerun adds that many again.
            if (queued.Count >= limit) break;
            if (!seen.Add(asset.Id)) continue;

            if (ImmichExclusion.Reason(asset, cutoff, settings.Immich.SkipFavorites) is { } exclusion)
            {
                excluded++;
                progress?.Report(new ImmichDiscoveryProgress.AssetExcluded(asset.OriginalFileName, asset.Id, exclusion));
                continue;
            }

            // An open video, held ones included, is never queued twice.
            if (history.OpenAssets.Contains(asset.Id)) continue;
            if (history.IsConverted(asset)) { alreadyConverted++; continue; }
            if (history.IsSettledSkip(asset, qualityFingerprint)) { previouslySkipped++; continue; }
            if (history.IsSettledFailure(asset)) { previouslyFailed++; continue; }
            if (history.IsSettledRejection(asset)) { previouslyRejected++; continue; }
            if (skips.TryGetValue(asset.Id, out var skip) && skip.IsCurrent(asset.Checksum, rulesFingerprint)) { previouslySkipped++; continue; }

            if (!settings.Eligibility.Extensions.Contains(Path.GetExtension(asset.OriginalFileName), StringComparer.OrdinalIgnoreCase) ||
                asset.Exif.FileSizeInBytes < minimumSize || asset.Exif.FileSizeInBytes <= minimumBytesSaved)
            {
                ineligible++;
                // A stale skip this check settles without a download would otherwise stay stale for good, and every later run
                // would announce it as one to check again.
                // Facts probed from the same bytes still hold; nothing was downloaded to replace them.
                if (skip is not null)
                    await jobs.PutImmichSkipAsync(databasePath, new ImmichSkipRecord(asset.Id, asset.Checksum, rulesFingerprint, "too small or not a listed file type", _utcNow(),
                        string.Equals(skip.Checksum, asset.Checksum, StringComparison.Ordinal) ? skip.VideoFacts : null), cancellationToken);
                continue;
            }

            if (StagedPath(settings.Immich.StagingDirectory, asset) is not { } stagedPath)
            {
                issues++;
                progress?.Report(new ImmichProgress.AssetIssue(asset.OriginalFileName, "the name Immich gives it can't be saved safely on this PC; left alone"));
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
                if (!string.Equals(await ImmichStaging.Sha1Base64Async(stagedPath, cancellationToken), asset.Checksum, StringComparison.Ordinal))
                {
                    issues++;
                    progress?.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Issue, "the download does not match Immich's checksum", RetryNextRun, 0));
                    continue;
                }

                var media = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(stagedPath, cancellationToken);
                // The size of the bytes just probed, which is what the rules were judged on.
                var downloadedBytes = new FileInfo(stagedPath).Length;
                var facts = new CandidateFacts(media.PrimaryVideoCodec, media.PrimaryVideoWidth, media.PrimaryVideoHeight, media.PrimaryVideoBitrate, downloadedBytes);
                var videoFacts = media.ToVideoFacts(downloadedBytes);
                if (EligibilityEvaluator.MatchingRule(downloadedBytes, media, settings.Eligibility) is not { } rule)
                {
                    var reason = EligibilityEvaluator.DescribeMismatch(downloadedBytes, media, settings.Eligibility);
                    await jobs.PutImmichSkipAsync(databasePath, new ImmichSkipRecord(asset.Id, asset.Checksum, rulesFingerprint, reason, _utcNow(), videoFacts), cancellationToken);
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
                    ImmichChecksum = asset.Checksum,
                    VideoFacts = videoFacts
                };
                JobCompatibility.Apply(job, JobCompatibility.ExpectedBinding(job.Id, settings));
                try
                {
                    queued.Add(await jobs.CreateAsync(databasePath, job, cancellationToken));
                }
                catch (OpenJobExistsException)
                {
                    // A queue retry from another terminal queued the asset since the history was read. Its job reads the
                    // same staged path, so the copy stays, and the asset is queued either way.
                    alreadyQueued++;
                }
                keepStaged = true;
                progress?.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, queued.Count, facts, rule));
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
                if (!keepStaged) ImmichStaging.DeleteStaged(stagedPath);
            }
        }

        return new ImmichDiscoveryResult(queued, alreadyQueued, history.Held, history.ReadyToFinalize, alreadyConverted, previouslyFailed, previouslySkipped, previouslyRejected, excluded, ineligible, skipped, issues,
            [.. skipReasons.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new SkipReasonCount(pair.Key, pair.Value))]);
    }

    // An issue is not recorded as a skip, so the next run downloads the video again.
    private const string RetryNextRun = "it will be tried again next run";

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
}
