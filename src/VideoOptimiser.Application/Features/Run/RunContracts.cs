using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Run;

public sealed record JobProcessingResult(JobRecord Job, ExitCode ExitCode, string Message);

public interface IJobProcessor
{
    /// <summary>
    /// Runs a queued job, read just before, through the stages it has left. <see cref="JobRecord.Forced"/> skips the
    /// watch-root and rules checks. A write that finds the job changed since it was read (held or dropped from another
    /// terminal) throws <see cref="JobChangedException"/>, which the run takes as "pass over it".
    /// </summary>
    Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Whether <see cref="ProcessAsync"/> would reach a remote stage for this job. Read-only: a run asks before creating a server.</summary>
    Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default);
}

public sealed record FailedQueueJob(Guid Id, string SourcePath, string FailureCategory, string FailureMessage);

/// <summary>A job that ended <see cref="JobStatus.Skipped"/>. Not a failure: it changes neither the exit code nor the managed worker's deletion.</summary>
public sealed record SkippedQueueJob(Guid Id, string SourcePath, string Message);

/// <param name="SkippedJobs">Started jobs left untouched because <c>--local</c> or <c>--remote</c> chose the other mode; see
/// <see cref="JobCompatibility.IsBoundToOtherMode"/>. They are neither runnable nor failed.</param>
/// <param name="InterruptedFinalizations">File jobs whose finalisation Ctrl+C or a crash interrupted. Left as they are: only
/// <c>finalize</c> can tell whether anything moved (VOPT-70).</param>
/// <param name="RunnableJobs">In run order: the run's first view of the queue.</param>
/// <param name="FollowQueue">The run takes each next video from the queue as it is then, so a video added, released or moved
/// next during the run joins it. False keeps the run to <paramref name="RunnableJobs"/>: <c>immich run</c>'s set, fixed by its
/// <c>--limit</c> (until phase A step 7).</param>
/// <param name="SourceKind">The source the run converts; null for every source (<c>run</c>).</param>
/// <param name="Held">The held videos of that source that a run would otherwise take.</param>
public sealed record QueuePreparationResult(IReadOnlyList<JobRecord> RunnableJobs, IReadOnlyList<FailedQueueJob> FailedJobs, IReadOnlyList<JobRecord> SkippedJobs, IReadOnlyList<JobRecord> InterruptedFinalizations, JobSourceKind? SourceKind = null, bool FollowQueue = true, int Held = 0);

/// <param name="UnreachableWorkerJob">The job the run stopped at because the worker could not be reached, or null. Every later
/// job would fail the same way, so the run ends there (VOPT-69). The job is <see cref="JobStatus.Interrupted"/> and resumes from
/// its stage, so it is not in <paramref name="FailedJobs"/>.</param>
/// <param name="NotStarted">The jobs after <paramref name="UnreachableWorkerJob"/> or <paramref name="StoppedAfter"/> that the run never started; they stay queued.</param>
/// <param name="StoppedAfter">The last job of a run that Ctrl+C stopped after it, with jobs left to start, or null.</param>
public sealed record QueueRunResult(int ReadyToFinalize, IReadOnlyList<FailedQueueJob> FailedJobs, IReadOnlyList<SkippedQueueJob> SkippedJobs, JobRecord? UnreachableWorkerJob, int NotStarted, ExitCode ExitCode, JobRecord? StoppedAfter = null)
{
    public int Failed => FailedJobs.Count;
}

public static class QueueExitCode
{
    /// <param name="workerUnreachable">Counts as one failure, so a run the worker stopped exits as it did when that job was
    /// listed among the failures.</param>
    public static ExitCode Calculate(int readyToFinalize, int failures, bool workerUnreachable = false) => failures == 0 && !workerUnreachable
        ? ExitCode.Success
        : readyToFinalize > 0 ? ExitCode.PartialSuccess : ExitCode.ProcessingFailure;
}

public interface IQueueService
{
    /// <summary>
    /// Readies the open jobs of one source, or of every source, for processing: <c>run</c> takes every job, <c>immich run</c>
    /// Immich jobs. Every source's Immich finalisation that a crash interrupted goes back to ready either way.
    /// </summary>
    /// <param name="sourceKind">The source to ready; null for every source.</param>
    /// <param name="modeOverridden">Whether <c>--local</c> or <c>--remote</c> set the mode in <paramref name="settings"/>. The settings
    /// alone cannot say so, and only then is a started job from the other mode skipped instead of failed.</param>
    Task<QueuePreparationResult> PrepareAsync(string databasePath, AppSettings settings, JobSourceKind? sourceKind, bool modeOverridden = false, CancellationToken cancellationToken = default);
    /// <summary>
    /// Converts the queued videos of <paramref name="preparation"/>'s source (every source when it has none), one at a time, each tried at most once: the
    /// first queued, unheld one in <see cref="JobRunOrder"/> that this run has not tried, read again as it starts.
    /// </summary>
    /// <param name="passOver">Asked before each video starts; true leaves it queued for a later run, after the caller has said why.</param>
    /// <param name="stopAfterThisVideo">Checked before each job, never during one, so a stop after this video keeps the current job's work.</param>
    Task<QueueRunResult> RunAsync(string databasePath, AppSettings settings, QueuePreparationResult preparation, Func<JobRecord, CancellationToken, Task<bool>>? passOver = null, IProgress<JobProgress>? progress = null, CancellationToken stopAfterThisVideo = default, CancellationToken cancellationToken = default);
}
