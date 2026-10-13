using VideoOptimiser.Application.Calibration;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Infrastructure.Immich;

namespace VideoOptimiser.Infrastructure.Calibration;

/// <summary>
/// Gathers what <see cref="CalibrationReport.Build"/> needs without side effects: the watch roots scanned as <c>queue
/// discover</c> scans them, Immich searched as <c>immich run</c> searches it, and the finished jobs. Its scanner and
/// repository must be the read-only ones (<see cref="UnsavedProbeCache"/>, a read-only <see cref="Jobs.SqliteJobRepository"/>):
/// this class stores nothing itself, and relies on them not to.
/// </summary>
public sealed class CalibrationService(IFileScanner scanner, IJobRepository jobs, IImmichClientFactory immichClients, Func<DateTimeOffset>? utcNow = null) : ICalibrationService
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// The service as the CLI runs it: a read-only store that migrates nothing, and a probe cache that never saves. Built
    /// here rather than in the CLI so tests check the same wiring the command uses.
    /// </summary>
    public static CalibrationService ReadOnly(IFileReadinessService readiness, Func<string, IMediaProbe> mediaProbes, IImmichClientFactory immichClients, Func<DateTimeOffset>? utcNow = null)
    {
        var schemaCheck = new Diagnostics.SqliteSchemaCheck();
        var scanner = new Scanning.FileScanner(readiness, mediaProbes, new UnsavedProbeCache(new Scanning.SqliteMediaProbeCache(schemaCheck, Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly)));
        return new CalibrationService(scanner, new Jobs.SqliteJobRepository(schemaCheck, Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly), immichClients, utcNow);
    }

    public async Task<CalibrationRun> RunAsync(AppSettings settings, IProgress<ScanProgress>? scanProgress, IProgress<int>? immichProgress, CancellationToken cancellationToken = default)
    {
        var databasePath = settings.Database.Path;
        // A missing database is no history, not an error: calibrate before the first run is the point of the command.
        // Read before scanning, so an outdated database stops the run before minutes of probing.
        var hasDatabase = File.Exists(databasePath);
        var finished = hasDatabase ? await jobs.ListAsync(databasePath, terminal: true, cancellationToken) : [];
        var open = hasDatabase ? await jobs.ListAsync(databasePath, terminal: false, cancellationToken) : [];

        var videos = new List<CalibrationVideo>();
        var scanIssues = 0;
        if (settings.Watch.Roots.Count > 0)
        {
            var scan = await scanner.ScanAsync(settings.Watch.Roots, settings, progress: scanProgress, cancellationToken: cancellationToken);
            scanIssues = scan.Issues.Count;
            foreach (var item in scan.Items)
            {
                if (item.MediaInfo is not { } media || item.SizeBytes is not { } size) continue;
                videos.Add(new CalibrationVideo(Path.GetFileName(item.Path), item.Path, null, media.PrimaryVideoCodec, media.PrimaryVideoWidth, media.PrimaryVideoHeight, media.PrimaryVideoBitrate, size));
            }
        }

        if (settings.Immich.IsConfigured)
        {
            var skips = hasDatabase ? await jobs.ListImmichSkipsAsync(databasePath, cancellationToken) : new Dictionary<string, ImmichSkipRecord>();
            videos.AddRange(await ImmichVideosAsync(settings, hasDatabase, [.. finished, .. open], skips, immichProgress, cancellationToken));
        }

        return new CalibrationRun(CalibrationReport.Build(settings, videos, finished, QualityFingerprint.Compute(settings)), scanIssues);
    }

    /// <summary>
    /// The assets <c>immich run</c> would judge, with the facts a job or skip probed from the same file when there are any.
    /// Without them only Immich's metadata is known: no codec, and a bitrate from the whole file's size and duration.
    /// </summary>
    private async Task<List<CalibrationVideo>> ImmichVideosAsync(AppSettings settings, bool hasDatabase, IReadOnlyList<JobRecord> allJobs, IReadOnlyDictionary<string, ImmichSkipRecord> skips, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var history = hasDatabase ? await ImmichDiscoveryService.ImmichHistory.LoadAsync(jobs, settings.Database.Path, cancellationToken) : new ImmichDiscoveryService.ImmichHistory();
        var jobFacts = allJobs
            .Where(job => job.ImmichAssetId is not null && job.ImmichChecksum is not null && job.VideoFacts is not null)
            .GroupBy(job => (job.ImmichAssetId!, job.ImmichChecksum!))
            .ToDictionary(group => group.Key, group => group.MaxBy(job => job.CreatedUtc)!.VideoFacts!);

        var client = immichClients.Create(settings.Immich);
        var cutoff = _utcNow().AddDays(-settings.Immich.MinimumAgeDays);
        var videos = new List<CalibrationVideo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var asset in client.SearchVideosAsync(cutoff, includeFavorites: !settings.Immich.SkipFavorites, cancellationToken))
        {
            if (!seen.Add(asset.Id) || ImmichDiscoveryService.Exclusion(asset, cutoff, settings.Immich.SkipFavorites) is not null) continue;
            if (!settings.Eligibility.Extensions.Contains(Path.GetExtension(asset.OriginalFileName), StringComparer.OrdinalIgnoreCase)) continue;
            progress?.Report(videos.Count + 1);

            var facts = jobFacts.GetValueOrDefault((asset.Id, asset.Checksum)) ??
                (skips.TryGetValue(asset.Id, out var skip) && string.Equals(skip.Checksum, asset.Checksum, StringComparison.Ordinal) ? skip.VideoFacts : null);
            var size = asset.Exif.FileSizeInBytes ?? facts?.SizeBytes ?? 0;
            videos.Add(history.IsConverted(asset)
                ? new CalibrationVideo(asset.OriginalFileName, null, asset.Id, null, asset.Width, asset.Height, null, size, AlreadyConverted: true)
                : facts is not null
                    ? new CalibrationVideo(asset.OriginalFileName, null, asset.Id, facts.Codec, facts.Width, facts.Height, facts.Bitrate, size)
                    : new CalibrationVideo(asset.OriginalFileName, null, asset.Id, null, asset.Width, asset.Height, OverallBitrate(size, asset.DurationMilliseconds), size));
        }

        return videos;
    }

    private static long? OverallBitrate(long sizeBytes, long? durationMilliseconds) =>
        sizeBytes > 0 && durationMilliseconds is > 0 and { } duration ? sizeBytes * 8 * 1000 / duration : null;
}

/// <summary>
/// The probe cache as <c>calibrate</c> uses it: read when the database exists, and never written, so files probed during the
/// report stay unknown to the next run. Storing them would break the command's promise to write nothing.
/// </summary>
public sealed class UnsavedProbeCache(IMediaProbeCache inner) : IMediaProbeCache
{
    public async Task<IReadOnlyList<MediaProbeCacheEntry>> LoadAllAsync(string databasePath, CancellationToken cancellationToken = default) =>
        File.Exists(databasePath) ? await inner.LoadAllAsync(databasePath, cancellationToken) : [];

    public Task StoreAsync(string databasePath, MediaProbeCacheEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
