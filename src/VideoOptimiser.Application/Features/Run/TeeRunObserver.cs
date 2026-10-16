using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// Forwards every event to two observers, the primary first. What the flow needs back (the failure's exit code, the progress
/// sinks) comes from the primary, so adding a second observer to a front end, such as the event file beside the console, can
/// never change what that front end does. The sinks it returns report to both.
/// </summary>
public sealed class TeeRunObserver(IRunObserver primary, IRunObserver secondary) : IRunObserver
{
    public void VideoSkipped(JobRecord job)
    {
        primary.VideoSkipped(job);
        secondary.VideoSkipped(job);
    }

    public void FinalisationInterrupted(JobRecord job)
    {
        primary.FinalisationInterrupted(job);
        secondary.FinalisationInterrupted(job);
    }

    public void VideosHeld(int count)
    {
        primary.VideosHeld(count);
        secondary.VideosHeld(count);
    }

    public IProgress<ImmichProgress> ImmichRestaging() => new TeeProgress<ImmichProgress>(primary.ImmichRestaging(), secondary.ImmichRestaging());

    public void ImmichVideosStayQueued(int count, string? unusableReason)
    {
        primary.ImmichVideosStayQueued(count, unusableReason);
        secondary.ImmichVideosStayQueued(count, unusableReason);
    }

    public void WorkerProgress(string line)
    {
        primary.WorkerProgress(line);
        secondary.WorkerProgress(line);
    }

    public IProgress<JobProgress> ConversionStarted(int count, bool workerUp)
    {
        var first = primary.ConversionStarted(count, workerUp);
        return new TeeProgress<JobProgress>(first, secondary.ConversionStarted(count, workerUp));
    }

    public void ConversionEnded()
    {
        primary.ConversionEnded();
        secondary.ConversionEnded();
    }

    public void JoiningVideoSkipped(JobRecord job)
    {
        primary.JoiningVideoSkipped(job);
        secondary.JoiningVideoSkipped(job);
    }

    public void JoiningVideoWaitsForWorker(JobRecord job)
    {
        primary.JoiningVideoWaitsForWorker(job);
        secondary.JoiningVideoWaitsForWorker(job);
    }

    public void StoppedByUnreachableWorker(string workerName, int notConverted)
    {
        primary.StoppedByUnreachableWorker(workerName, notConverted);
        secondary.StoppedByUnreachableWorker(workerName, notConverted);
    }

    public void StoppedAfter(JobRecord last, int notStarted)
    {
        primary.StoppedAfter(last, notStarted);
        secondary.StoppedAfter(last, notStarted);
    }

    public void Summary(int ready, int skipped, IReadOnlyList<FailedQueueJob> failures, int readyTotal, bool stoppedAfter)
    {
        primary.Summary(ready, skipped, failures, readyTotal, stoppedAfter);
        secondary.Summary(ready, skipped, failures, readyTotal, stoppedAfter);
    }

    public void StoppedWithoutManagedWorker(JobInterruptedException? interrupted, string? manualHost)
    {
        primary.StoppedWithoutManagedWorker(interrupted, manualHost);
        secondary.StoppedWithoutManagedWorker(interrupted, manualHost);
    }

    public void StoppedNow(JobInterruptedException? interrupted, string worker, WorkerFate fate, bool settingUpWorker)
    {
        primary.StoppedNow(interrupted, worker, fate, settingUpWorker);
        secondary.StoppedNow(interrupted, worker, fate, settingUpWorker);
    }

    public void WorkerUnreachableAtEnd(JobRecord job, bool questionFollows)
    {
        primary.WorkerUnreachableAtEnd(job, questionFollows);
        secondary.WorkerUnreachableAtEnd(job, questionFollows);
    }

    public void WorkerKept(string worker, RunEnd runEnd, JobStatus? remoteStage, bool answeredNo)
    {
        primary.WorkerKept(worker, runEnd, remoteStage, answeredNo);
        secondary.WorkerKept(worker, runEnd, remoteStage, answeredNo);
    }

    public void WorkerDeleting()
    {
        primary.WorkerDeleting();
        secondary.WorkerDeleting();
    }

    public void WorkerDeleteFailed(string worker, string message)
    {
        primary.WorkerDeleteFailed(worker, message);
        secondary.WorkerDeleteFailed(worker, message);
    }

    public void WorkerDeleteEnded()
    {
        primary.WorkerDeleteEnded();
        secondary.WorkerDeleteEnded();
    }

    public void ManualWorkerUnreachable(JobRecord job)
    {
        primary.ManualWorkerUnreachable(job);
        secondary.ManualWorkerUnreachable(job);
    }

    public void ManualWorkerReminder()
    {
        primary.ManualWorkerReminder();
        secondary.ManualWorkerReminder();
    }

    public int RunFailed(Exception exception)
    {
        var exitCode = primary.RunFailed(exception);
        _ = secondary.RunFailed(exception);
        return exitCode;
    }

    private sealed class TeeProgress<T>(IProgress<T> first, IProgress<T> second) : IProgress<T>
    {
        public void Report(T value)
        {
            first.Report(value);
            second.Report(value);
        }
    }
}
