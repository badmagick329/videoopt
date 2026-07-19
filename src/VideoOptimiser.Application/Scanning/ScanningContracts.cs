using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Scanning;

public enum ScanItemStatus
{
    Eligible,
    Ineligible,
    WaitingForStability,
    ProbeFailed
}

public sealed record MediaInfo(
    string PrimaryVideoCodec,
    int VideoStreamCount,
    int AudioStreamCount,
    int SubtitleStreamCount,
    int AttachmentCount,
    double? DurationSeconds,
    long? SizeBytes);

public sealed record StabilityResult(bool IsStable, string Reason);

public sealed record ScanItem(
    string Path,
    ScanItemStatus Status,
    string Reason,
    long? SizeBytes = null,
    MediaInfo? MediaInfo = null);

public sealed record ScanIssue(string Path, string Message);

public sealed record ScanProgress(string Path, string Stage, string Message);

public sealed record ScanReport(IReadOnlyList<ScanItem> Items, IReadOnlyList<ScanIssue> Issues)
{
    public int EligibleCount => Items.Count(item => item.Status == ScanItemStatus.Eligible);
}

public interface IFileStabilityService
{
    Task<StabilityResult> WaitUntilStableAsync(
        string path,
        StabilitySettings settings,
        bool requireRepeatedObservations = true,
        CancellationToken cancellationToken = default);
}

public interface IMediaProbe
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

public interface IFileScanner
{
    Task<ScanReport> ScanAsync(
        IReadOnlyList<WatchRootSettings> roots,
        AppSettings settings,
        bool useImmediateStabilityCheck = false,
        bool stopAfterFirstEligible = false,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
