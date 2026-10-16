using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// What <see cref="RunFlow"/> tells its front end as a run goes, in the order it happens. Events carry facts (jobs, counts,
/// stages, fates), never wording: the CLI owns every line, and a TUI or a runner with no terminal chooses its own. A
/// front end that shows nothing implements them all as no-ops. The two methods that return something are the ones the flow
/// needs a value from: a progress sink and the exit code of a failure.
/// </summary>
public interface IRunObserver
{
    /// <summary>A started video left untouched because <c>--local</c> or <c>--remote</c> chose the other mode.</summary>
    void VideoSkipped(JobRecord job);

    /// <summary>A file video whose finalisation was interrupted; only <c>finalize</c> can tell whether anything moved (VOPT-70).</summary>
    void FinalisationInterrupted(JobRecord job);

    /// <summary>Held videos that a run would otherwise take.</summary>
    void VideosHeld(int count);

    /// <summary>The sink for one restaging call: Immich downloads the missing copies of the videos it is about to convert.</summary>
    IProgress<ImmichProgress> ImmichRestaging();

    /// <summary>Immich videos that stay queued this run.</summary>
    /// <param name="unusableReason">Why Immich can't be used, after a restaging call; null when Immich is not configured at all.</param>
    void ImmichVideosStayQueued(int count, string? unusableReason);

    /// <summary>A line from creating, bootstrapping or deleting the managed worker.</summary>
    void WorkerProgress(string line);

    /// <summary>The conversion is about to start; the returned sink takes every job's progress until <see cref="ConversionEnded"/>.</summary>
    /// <param name="workerUp">A managed worker is recorded, so a first stop request may ask what to do with it.</param>
    IProgress<JobProgress> ConversionStarted(int count, bool workerUp);

    /// <summary>Raised in a <c>finally</c>: a cancel or an error ends the conversion's scope exactly where it ended.</summary>
    void ConversionEnded();

    /// <summary>A video that joined the run after it prepared and is started in the other mode.</summary>
    void JoiningVideoSkipped(JobRecord job);

    /// <summary>A video that joined the run after it prepared and needs a worker this run has none of.</summary>
    void JoiningVideoWaitsForWorker(JobRecord job);

    /// <param name="notConverted">The interrupted video plus the ones never started.</param>
    void StoppedByUnreachableWorker(string workerName, int notConverted);

    /// <summary>The run stopped after <paramref name="last"/>, with <paramref name="notStarted"/> videos left.</summary>
    void StoppedAfter(JobRecord last, int notStarted);

    /// <summary>The run's outcome, after the conversion and before the worker's fate.</summary>
    /// <param name="readyTotal">Videos ready to finalise in the queue, those earlier runs left included; never fewer than <paramref name="ready"/>.</param>
    /// <param name="stoppedAfter">The run stopped after a video, with videos left to start.</param>
    void Summary(int ready, int skipped, IReadOnlyList<FailedQueueJob> failures, int readyTotal, bool stoppedAfter);

    /// <summary>A stop now with no managed worker recorded.</summary>
    /// <param name="interrupted">The video it stopped, or null when none was converting.</param>
    /// <param name="manualHost">The SSH host, when the mode is remote and the worker is not managed; otherwise null.</param>
    void StoppedWithoutManagedWorker(JobInterruptedException? interrupted, string? manualHost);

    /// <summary>A stop now with a managed worker recorded, before its fate is carried out. <paramref name="fate"/> is <see cref="WorkerFate.Ask"/> when a question follows.</summary>
    /// <param name="settingUpWorker">The stop came while the worker was still being created or bootstrapped.</param>
    void StoppedNow(JobInterruptedException? interrupted, string worker, WorkerFate fate, bool settingUpWorker);

    /// <summary>The managed worker stopped answering and the run's end does not delete it.</summary>
    /// <param name="questionFollows">The fate is <see cref="WorkerFate.Ask"/>: the question replaces the last line.</param>
    void WorkerUnreachableAtEnd(JobRecord job, bool questionFollows);

    /// <summary>The managed worker stays up. <paramref name="answeredNo"/> is true when the user was just asked and said no.</summary>
    void WorkerKept(string worker, RunEnd runEnd, JobStatus? remoteStage, bool answeredNo);

    /// <summary>The delete is about to start; until <see cref="WorkerDeleteEnded"/> a stop request must not cut it off.</summary>
    void WorkerDeleting();

    void WorkerDeleteFailed(string worker, string message);

    /// <summary>Raised in a <c>finally</c>, after <see cref="WorkerDeleteFailed"/> when the delete failed.</summary>
    void WorkerDeleteEnded();

    /// <summary>A manual host stopped answering; the run neither bills nor deletes it, so the user hears only how to resume.</summary>
    void ManualWorkerUnreachable(JobRecord job);

    /// <summary>A remote run on a manual host ended: the worker is the user's to delete.</summary>
    void ManualWorkerReminder();

    /// <summary>Reports an error that ended the run, or the worker's end; returns the exit code it maps to.</summary>
    int RunFailed(Exception exception);
}
