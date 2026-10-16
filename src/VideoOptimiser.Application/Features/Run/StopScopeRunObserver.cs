using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// Opens and closes the <see cref="RunStop"/> scopes around a conversion and a worker delete, as the console observer does for
/// Ctrl+C, so a stop request means what a press means at that moment: while a video converts, the first stops after it; during
/// the delete, none cuts it off. It prints and records nothing, so the runner pairs it with the event-file observer. All
/// other events are no-ops.
/// </summary>
public sealed class StopScopeRunObserver(RunStop stop) : IRunObserver
{
    private IDisposable? _convertingScope;
    private IDisposable? _deletingScope;

    /// <remarks>There is no console to tell, so the scope's callback does nothing; the press itself cancels the after-video token.</remarks>
    public IProgress<JobProgress> ConversionStarted(int count, bool workerUp)
    {
        _convertingScope = stop.WhileConverting(() => { });
        return NoProgress<JobProgress>.Instance;
    }

    /// <remarks>No scope: Ctrl+C during the re-check is a stop now, and the re-check saves a verdict and its job together.</remarks>
    public IProgress<JobProgress> RecheckStarted(int count) => NoProgress<JobProgress>.Instance;

    public void Rechecked(int ready, int toCheck, int notWorthConverting, int stillFailed) { }

    public void ConversionEnded() => _convertingScope?.Dispose();

    public void WorkerDeleting() => _deletingScope = stop.WhileDeleting(() => { });

    public void WorkerDeleteEnded() => _deletingScope?.Dispose();

    public void VideoSkipped(JobRecord job) { }

    public void FinalisationInterrupted(JobRecord job) { }

    public void VideosHeld(int count) { }

    public IProgress<ImmichProgress> ImmichRestaging() => NoProgress<ImmichProgress>.Instance;

    public void ImmichVideosStayQueued(int count, string? unusableReason) { }

    public void WorkerProgress(string line) { }

    public void JoiningVideoSkipped(JobRecord job) { }

    public void JoiningVideoWaitsForWorker(JobRecord job) { }

    public void StoppedByUnreachableWorker(string workerName, int notConverted) { }

    public void StoppedAfter(JobRecord last, int notStarted) { }

    public void Summary(int ready, int skipped, IReadOnlyList<FailedQueueJob> failures, int readyTotal, bool stoppedAfter, int toCheck) { }

    public void StoppedWithoutManagedWorker(JobInterruptedException? interrupted, string? manualHost) { }

    public void StoppedNow(JobInterruptedException? interrupted, string worker, WorkerFate fate, bool settingUpWorker) { }

    public void WorkerUnreachableAtEnd(JobRecord job, bool questionFollows) { }

    public void WorkerKept(string worker, RunEnd runEnd, JobStatus? remoteStage, bool answeredNo) { }

    public void WorkerDeleteFailed(string worker, string message) { }

    public void ManualWorkerUnreachable(JobRecord job) { }

    public void ManualWorkerReminder() { }

    /// <summary>The exit code of an error is the other observer's to give; this one only has the flow's contract to satisfy.</summary>
    public int RunFailed(Exception exception) => (int)ExitCode.GeneralFailure;

    private sealed class NoProgress<T> : IProgress<T>
    {
        public static readonly NoProgress<T> Instance = new();

        public void Report(T value) { }
    }
}
