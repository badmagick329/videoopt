using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Queue;

/// <param name="AlreadyQueued">Eligible files whose open job is waiting or stopped, so a run takes it.</param>
/// <param name="Held">Eligible files whose open job is held.</param>
/// <param name="ReadyToFinalize">Eligible files whose open job is ready to finalise, being finalised or stopped while it was.</param>
public sealed record QueueDiscoveryResult(IReadOnlyList<string> QueuedPaths, int AlreadyQueued, int Issues, int CacheHits = 0, int RealProbes = 0, int AlreadyConverted = 0, int PreviouslyFailed = 0, int PreviouslySkipped = 0, int Held = 0, int ReadyToFinalize = 0);

/// <summary>What <c>queue add &lt;file&gt;</c> found. Each outcome but <see cref="Queued"/> leaves the queue as it was.</summary>
public enum QueueAddOutcome
{
    Queued,
    Missing,
    /// <summary>A temporary output or a staged Immich copy: queueing it would convert the tool's own file.</summary>
    MadeByTool,
    Archived,
    OpenJob,
    /// <summary>Its latest job settled it (completed, failed, dropped, not worth converting) and the file is unchanged.</summary>
    Settled,
    OutsideRoots,
    Ineligible,
    TooSmall,
    Unreadable,
    ProbeFailed
}

/// <summary>Whether a run going now takes videos queued from here on.</summary>
public enum NextRun
{
    NoRun,
    /// <summary>A <c>run</c> takes each next video from the queue as it is.</summary>
    RunTakesThem,
    /// <summary>An <c>immich run</c> keeps the videos it started with, as does a run from an older version, so they wait for it to end.</summary>
    AfterRunEnds
}

/// <summary>The one place that reads a live run's name as what it means for videos queued now.</summary>
public static class NextRunRule
{
    /// <param name="liveRun">How the run going now names itself (<see cref="Jobs.IDatabaseLocks.LiveRun"/>), or null.</param>
    public static NextRun Decide(string? liveRun) => liveRun switch
    {
        null => NextRun.NoRun,
        Jobs.LockHolders.Run => NextRun.RunTakesThem,
        _ => NextRun.AfterRunEnds
    };
}

/// <summary>What an open job is really doing, as the lock files and its status say: a converting status with no run going is a stop a crash left.</summary>
public enum OpenJobState
{
    /// <summary>Ready to finalise, and its last finalise failed with a message.</summary>
    LastFinaliseFailed,
    Held,
    ReadyToFinalize,
    StoppedWhileFinalising,
    Finalising,
    /// <summary>Converting by its status, but no run holds the run lock.</summary>
    Stopped,
    /// <summary>Any other: the job's own status says it.</summary>
    Other
}

/// <param name="Job">The new job, the open job, or the job that settled the file.</param>
/// <param name="Reason">Why the file is ineligible, too small, unreadable or unprobeable.</param>
/// <param name="OutsideRules">Queued only because <c>--force</c> lifted the watch-root or rule checks.</param>
/// <param name="OpenState">For <see cref="QueueAddOutcome.OpenJob"/>: what the open job is doing now.</param>
public sealed record QueueAddResult(QueueAddOutcome Outcome, string Path, JobRecord? Job = null, string? Reason = null, bool OutsideRules = false, OpenJobState? OpenState = null);

/// <summary>Why <c>queue add</c> without a file scans nothing.</summary>
public enum QueueDiscoveryRefusal
{
    /// <summary>Settings validation allows no watch roots when Immich is configured; scanning nothing would only print "Queued: 0".</summary>
    NoWatchRoots
}

public interface IQueueDiscovery
{
    /// <summary>Whether <see cref="DiscoverAsync"/> has anything to scan; null when it has.</summary>
    QueueDiscoveryRefusal? Refusal(AppSettings settings);

    Task<QueueDiscoveryResult> DiscoverAsync(string databasePath, AppSettings settings, bool first, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues one file after the checks a run makes: inside a watch root, eligible, worth converting. <paramref name="force"/>
    /// skips the watch-root and rule checks and is stored on the job; nothing admits a file the tool made or archived.
    /// </summary>
    Task<QueueAddResult> AddFileAsync(string databasePath, AppSettings settings, string path, bool force, CancellationToken cancellationToken = default);

    /// <summary>
    /// The videos a run takes before <paramref name="job"/>: waiting and stopped ones, not held ones, not a finalisation.
    /// Asked after <see cref="AddFileAsync"/> so the "Queued" line is not held back by this read.
    /// </summary>
    /// <summary>What a run going now does with videos queued now; read when the CLI says what to run next.</summary>
    NextRun NextRun(string databasePath);

    Task<int> AheadOfAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
}

/// <param name="DownloadedCopyGone">An Immich replacement whose staged copy is no longer on disk, so the run downloads it again first.</param>
public sealed record JobRetryResult(JobRecord? Replacement, string? Error, bool DownloadedCopyGone = false)
{
    public bool Succeeded => Replacement is not null;
}

public interface IJobRetryService
{
    Task<JobRetryResult> RetryAsync(string databasePath, Guid jobId, AppSettings settings, CancellationToken cancellationToken = default);
}
