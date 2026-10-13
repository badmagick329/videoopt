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
    long? PrimaryVideoBitrate = null,
    IReadOnlyList<string>? AudioCodecs = null);

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

/// <summary>
/// A scan event: an <c>Issue</c> with its message, or a <c>Checked</c> tick after each file with the running
/// <paramref name="FilesChecked"/> and <paramref name="Probed"/> counts. Ticks carry counts rather than text so the caller
/// decides how often to print them.
/// </summary>
public sealed record ScanProgress(string Path, string Stage, string Message, int FilesChecked = 0, int Probed = 0);

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

/// <summary>
/// ffprobe could not read a media file: it exited non-zero, or reported nothing usable. A dedicated type, rather than
/// <see cref="InvalidDataException"/>, because the CLI reads that one as an unreadable configuration file.
/// </summary>
public sealed class MediaProbeException(string message, Exception? innerException = null) : Exception(message, innerException);

public interface IMediaProbeCache
{
    Task<IReadOnlyList<MediaProbeCacheEntry>> LoadAllAsync(
        string databasePath,
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
