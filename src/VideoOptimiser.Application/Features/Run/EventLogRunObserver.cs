using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// Writes what <see cref="RunFlow"/> reports as <see cref="RunEvent"/> lines in the run's event file, facts only: a TUI or
/// any other reader words them. Every call is one event, in the order the flow raised it. Background and foreground runs both
/// use it, so a TUI open in another window follows either.
/// </summary>
/// <remarks>
/// ab-av1 reports its encode several times a second, and each line is a flushed write to a file that other processes read, so
/// <see cref="JobProgressKind.ToolOutput"/> lines are written at most once a second. Every other kind is a step or an outcome
/// and is always written. One per command, registered as a singleton; progress arrives from several threads.
/// </remarks>
public sealed class EventLogRunObserver(IRunEventLog log, TimeProvider time) : IRunObserver
{
    private static readonly TimeSpan ToolOutputInterval = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private DateTimeOffset? _lastToolOutput;

    public void VideoSkipped(JobRecord job) => Write(RunEventKind.VideoSkipped, jobId: job.Id, sourcePath: job.SourcePath);

    public void FinalisationInterrupted(JobRecord job) => Write(RunEventKind.FinalisationInterrupted, jobId: job.Id, sourcePath: job.SourcePath);

    public void VideosHeld(int count) => Write(RunEventKind.VideosHeld, count: count);

    /// <summary>
    /// The three restaging facts share one event kind. <see cref="RunEvent.SourcePath"/> holds the Immich file name (the video
    /// has no path until its copy is back). <see cref="RunEvent.Flag"/> is null while downloading, true once the copy is back,
    /// false when there is a problem, which <see cref="RunEvent.Text"/> names.
    /// </summary>
    public IProgress<ImmichProgress> ImmichRestaging() => new SynchronousProgress<ImmichProgress>(progress =>
    {
        switch (progress)
        {
            case ImmichProgress.Restaging restaging:
                Write(RunEventKind.ImmichRestaging, jobId: restaging.JobId, sourcePath: restaging.FileName);
                break;
            case ImmichProgress.Restaged restaged:
                Write(RunEventKind.ImmichRestaging, sourcePath: restaged.FileName, text: restaged.Problem, flag: restaged.Problem is null);
                break;
            case ImmichProgress.AssetIssue issue:
                Write(RunEventKind.ImmichRestaging, sourcePath: issue.FileName, text: issue.Message, flag: false);
                break;
        }
    });

    public void ImmichVideosStayQueued(int count, string? unusableReason) => Write(RunEventKind.ImmichVideosStayQueued, count: count, text: unusableReason);

    public void WorkerProgress(string line) => Write(RunEventKind.WorkerProgress, text: line);

    public IProgress<JobProgress> ConversionStarted(int count, bool workerUp)
    {
        Write(RunEventKind.ConversionStarted, count: count, flag: workerUp);
        lock (_gate) _lastToolOutput = null;
        return new SynchronousProgress<JobProgress>(ReportProgress);
    }

    public void ConversionEnded() => Write(RunEventKind.ConversionEnded);

    public void JoiningVideoSkipped(JobRecord job) => Write(RunEventKind.JoiningVideoSkipped, jobId: job.Id, sourcePath: job.SourcePath);

    public void JoiningVideoWaitsForWorker(JobRecord job) => Write(RunEventKind.JoiningVideoWaitsForWorker, jobId: job.Id, sourcePath: job.SourcePath);

    public void StoppedByUnreachableWorker(string workerName, int notConverted) => Write(RunEventKind.StoppedByUnreachableWorker, worker: workerName, count: notConverted);

    public void StoppedAfter(JobRecord last, int notStarted) => Write(RunEventKind.StoppedAfter, jobId: last.Id, sourcePath: last.SourcePath, count: notStarted);

    public void Summary(int ready, int skipped, IReadOnlyList<FailedQueueJob> failures, int readyTotal, bool stoppedAfter) =>
        Write(RunEventKind.Summary, count: readyTotal, ready: ready, skipped: skipped, flag: stoppedAfter, failures: failures);

    public void StoppedWithoutManagedWorker(JobInterruptedException? interrupted, string? manualHost) =>
        Write(RunEventKind.StoppedWithoutManagedWorker, jobId: interrupted?.Job.Id, sourcePath: interrupted?.Job.SourcePath, worker: manualHost, stage: interrupted?.Stage.ToString());

    public void StoppedNow(JobInterruptedException? interrupted, string worker, WorkerFate fate, bool settingUpWorker) =>
        Write(RunEventKind.StoppedNow, jobId: interrupted?.Job.Id, sourcePath: interrupted?.Job.SourcePath, worker: worker, fate: fate.ToString(), stage: interrupted?.Stage.ToString(), flag: settingUpWorker);

    public void WorkerUnreachableAtEnd(JobRecord job, bool questionFollows) =>
        Write(RunEventKind.WorkerUnreachableAtEnd, jobId: job.Id, sourcePath: job.SourcePath, stage: job.ResumeStatus?.ToString(), flag: questionFollows);

    public void WorkerKept(string worker, RunEnd runEnd, JobStatus? remoteStage, bool answeredNo) =>
        Write(RunEventKind.WorkerKept, worker: worker, runEnd: runEnd.ToString(), stage: remoteStage?.ToString(), flag: answeredNo);

    public void WorkerDeleting() => Write(RunEventKind.WorkerDeleting);

    public void WorkerDeleteFailed(string worker, string message) => Write(RunEventKind.WorkerDeleteFailed, worker: worker, text: message);

    public void WorkerDeleteEnded() => Write(RunEventKind.WorkerDeleteEnded);

    public void ManualWorkerUnreachable(JobRecord job) => Write(RunEventKind.ManualWorkerUnreachable, jobId: job.Id, sourcePath: job.SourcePath);

    public void ManualWorkerReminder() => Write(RunEventKind.ManualWorkerReminder);

    /// <summary>The run has no console to print the error on, so the file is where it lands.</summary>
    public int RunFailed(Exception exception)
    {
        Write(RunEventKind.RunFailed, text: exception.GetBaseException().Message);
        return (int)ExitCode.GeneralFailure;
    }

    private void ReportProgress(JobProgress progress)
    {
        if (progress.Kind == JobProgressKind.ToolOutput)
        {
            lock (_gate)
            {
                var now = time.GetUtcNow();
                if (_lastToolOutput is { } last && now - last < ToolOutputInterval) return;
                _lastToolOutput = now;
            }
        }
        Write(RunEventKind.Progress, text: progress.Text, progress: progress.Kind.ToString(), tone: progress.Tone.ToString(), timeLeft: progress.TimeLeft, percent: progress.Percent);
    }

    private void Write(
        RunEventKind kind,
        Guid? jobId = null,
        string? sourcePath = null,
        int? count = null,
        int? ready = null,
        int? skipped = null,
        string? text = null,
        string? worker = null,
        string? fate = null,
        string? stage = null,
        string? runEnd = null,
        string? progress = null,
        string? tone = null,
        TimeSpan? timeLeft = null,
        bool? flag = null,
        IReadOnlyList<FailedQueueJob>? failures = null,
        int? percent = null) =>
        log.Append(new RunEvent(time.GetUtcNow(), kind, jobId, sourcePath, count, ready, skipped, text, worker, fate, stage, runEnd, progress, tone, timeLeft, flag, failures, percent));

    // Progress<T> posts to a thread-pool thread when there is no synchronisation context, which would reorder events the
    // flow raises around them.
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
