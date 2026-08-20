using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Scanning;

public enum ScanItemStatus
{
    Eligible,
    Ineligible,
    Unavailable,
    ProbeFailed
}

public sealed record MediaInfo(
    string PrimaryVideoCodec,
    int VideoStreamCount,
    int AudioStreamCount,
    int SubtitleStreamCount,
    int AttachmentCount,
    double? DurationSeconds,
    long? SizeBytes,
    int? PrimaryVideoWidth = null,
    int? PrimaryVideoHeight = null,
    long? PrimaryVideoBitrate = null);

public sealed record MediaTimeline(
    double FirstVideoTimestampSeconds,
    double LastVideoEndTimestampSeconds,
    long PrimaryVideoPacketCount,
    double? FirstPrimaryAudioTimestampSeconds)
{
    public double PrimaryVideoDurationSeconds => LastVideoEndTimestampSeconds - FirstVideoTimestampSeconds;
}

public sealed record FileReadinessResult(bool IsReady, string Reason);

public sealed record ScanItem(
    string Path,
    ScanItemStatus Status,
    string Reason,
    long? SizeBytes = null,
    MediaInfo? MediaInfo = null);

public sealed record ScanIssue(string Path, string Message);

public sealed record ScanProgress(string Path, string Stage, string Message);

public sealed record ScanReport(
    IReadOnlyList<ScanItem> Items,
    IReadOnlyList<ScanIssue> Issues,
    int CacheHits = 0,
    int RealProbes = 0)
{
    public int EligibleCount => Items.Count(item => item.Status == ScanItemStatus.Eligible);
}

public sealed record MediaProbeCacheEntry(
    string SourcePath,
    long SourceSizeBytes,
    long SourceLastWriteUtcTicks,
    MediaInfo MediaInfo);

public interface IFileReadinessService
{
    Task<FileReadinessResult> CheckAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public interface IMediaProbe
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

public interface IMediaTimelineProbe
{
    Task<MediaTimeline> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

public interface IMediaProbeCache
{
    Task<MediaInfo?> GetAsync(
        string databasePath,
        string sourcePath,
        long sourceSizeBytes,
        long sourceLastWriteUtcTicks,
        CancellationToken cancellationToken = default);

    Task StoreAsync(
        string databasePath,
        MediaProbeCacheEntry entry,
        CancellationToken cancellationToken = default);
}

public interface IFileScanner
{
    Task<ScanReport> ScanAsync(
        IReadOnlyList<WatchRootSettings> roots,
        AppSettings settings,
        bool stopAfterFirstEligible = false,
        IReadOnlySet<string>? openSourcePaths = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
