using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.Application.Eligibility;

public enum ScanItemStatus
{
    Eligible,
    Ineligible,
    Unavailable,
    ProbeFailed
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

public interface IFileReadinessService
{
    Task<FileReadinessResult> CheckAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public interface IFileScanner
{
    /// <param name="stopAfterEligible">
    /// Return once this many eligible files outside <paramref name="openSourcePaths"/> have been met, leaving the rest
    /// unchecked; null scans everything.
    /// </param>
    Task<ScanReport> ScanAsync(
        IReadOnlyList<WatchRootSettings> roots,
        AppSettings settings,
        int? stopAfterEligible = null,
        IReadOnlySet<string>? openSourcePaths = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The verdict a scan would give one file, for <c>queue add &lt;file&gt;</c>. With <paramref name="force"/> the extension,
    /// name, excluded-directory and rule checks are skipped, but the size cap still applies: no AV1 of a video that small
    /// could save <c>savings.minimumBytesSaved</c>. Nor does it lift the refusal of a video that is already AV1
    /// (<see cref="OutputCodec"/>). The caller checks the watch roots and the archive directory.
    /// </summary>
    Task<FileVerdict> JudgeAsync(string path, AppSettings settings, bool force, CancellationToken cancellationToken = default);
}

/// <param name="TooSmall">The size cap ruled the file out, which <c>--force</c> does not lift.</param>
public sealed record FileVerdict(ScanItem Item, bool TooSmall = false);
