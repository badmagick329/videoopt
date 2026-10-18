using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// What <c>run</c> and <c>immich run</c> share, without a terminal: the whole queue prepared and converted, folder and Immich
/// videos alike, the managed worker created only when a video needs it, and the decision about the worker's fate at every way
/// the run ends. It tells <see cref="IRunObserver"/> what happens and asks <see cref="IRunQuestions"/> what only a person can
/// answer, so the CLI, a TUI and a background runner drive the same flow (D3 in the vertical-slices ADR).
/// </summary>
/// <remarks>
/// One per command, registered as a singleton: it holds the run's id and worker state (<see cref="MarkStarted"/>, whether the
/// worker was used, set up or already decided), and a host serves one command. It writes the managed worker onto the run's
/// row and hands <see cref="QueueService"/> the run's <see cref="ConversionSpans"/>. The outcome is raised before the worker's fate is
/// decided, so the worker's events come last. The worker question and the delete run on their own token: the run's is already
/// cancelled after a stop, and a cancelled delete would leave the worker billing silently.
/// </remarks>
public sealed class RunFlow(IQueueService queue, IQueueDiscovery discovery, IJobProcessor processor, IRemoteWorkerLifecycle lifecycle, IImmichRestaging restaging, FailedValidationRecheck recheck, IRunRecordStore records, TimeProvider time)
{
    private bool _started;
    private Guid? _runId;
    private long? _workerServerId;
    private bool _used;
    private bool _settingUpWorker;
    private bool _concluded;

    /// <summary>Called once the run holds its lock: from then on every end decides the worker's fate (<see cref="GuardAsync"/>).</summary>
    /// <param name="runId">The run's row (<see cref="IRunRecordStore"/>), which its managed worker and conversion spans are written against.</param>
    public void MarkStarted(Guid runId)
    {
        _started = true;
        _runId = runId;
    }

    /// <summary>
    /// Runs a run command so that every way it ends, once it holds the run lock, decides the managed worker's fate: a stop
    /// now (Ctrl+C twice, or once with no video converting, whatever it interrupted: Immich discovery, the worker's creation,
    /// a prompt or a video), an error, or a return before anything was converted. A finished conversion decides inside
    /// <see cref="ConvertQueueAsync"/>, after its summary.
    /// </summary>
    /// <returns>The exit code <paramref name="run"/> returned, 9 for a stop now, or the code <see cref="IRunObserver.RunFailed"/> gave an error.</returns>
    public async Task<int> GuardAsync(AppSettings settings, IRunObserver observer, IRunQuestions questions, Func<Task<int>> run)
    {
        int exitCode;
        try
        {
            exitCode = await run();
        }
        catch (OperationCanceledException exception) when (_started && !_concluded)
        {
            // A failed delete still exits 9: the run stopped.
            await ConcludeOrReportAsync(observer, () => ConcludeAsync(settings, observer, questions, RunEnd.StoppedNow, interrupted: exception as JobInterruptedException));
            return (int)ExitCode.Cancelled;
        }
        catch (Exception exception) when (_started && !_concluded && exception is not OperationCanceledException)
        {
            // The error first, then what happens to the worker.
            exitCode = observer.RunFailed(exception);
            await ConcludeOrReportAsync(observer, () => ConcludeAsync(settings, observer, questions, RunEnd.Failed));
            return exitCode;
        }
        if (_started && !_concluded) await ConcludeOrReportAsync(observer, () => ConcludeAsync(settings, observer, questions, RunEnd.Finished));
        return exitCode;
    }

    /// <summary>
    /// Everything a run does after its banner: prepares the whole queue, readies its Immich videos, and converts it, creating a
    /// managed worker only when there is work for it. With nothing to convert, <see cref="GuardAsync"/> decides the fate.
    /// Before any worker is asked about or created, and even with nothing queued, videos that failed validation under older
    /// rules are checked again (<see cref="FailedValidationRecheck"/>, VOPT-119).
    /// </summary>
    /// <param name="modeOverridden">Whether <c>--local</c> or <c>--remote</c> set the mode in <paramref name="settings"/>.</param>
    /// <param name="stopAfterThisVideo">Cancelled by a first stop request: checked between videos, never during one.</param>
    public async Task<ExitCode> ConvertQueueAsync(AppSettings settings, bool modeOverridden, IRunObserver observer, IRunQuestions questions, CancellationToken stopAfterThisVideo, CancellationToken cancellationToken)
    {
        var preparation = await queue.PrepareAsync(settings.Database.Path, settings, sourceKind: null, modeOverridden, cancellationToken);
        await recheck.RecheckAsync(settings, observer, cancellationToken);
        var (ready, _, failures, workerUnreachable, stopped, workerHandled) = await ProcessPreparedJobsAsync(settings, modeOverridden, observer, questions, preparation, stopAfterThisVideo, cancellationToken);
        return stopped ? ExitCode.Cancelled : !workerHandled ? ExitCode.GeneralFailure : QueueExitCode.Calculate(ready, failures.Length, workerUnreachable);
    }

    private async Task<(int Ready, int Skipped, FailedQueueJob[] Failures, bool WorkerUnreachable, bool Stopped, bool WorkerHandled)> ProcessPreparedJobsAsync(AppSettings settings, bool modeOverridden, IRunObserver observer, IRunQuestions questions, QueuePreparationResult preparation, CancellationToken stopAfterThisVideo, CancellationToken cancellationToken)
    {
        foreach (var job in preparation.SkippedJobs) observer.VideoSkipped(job);
        // F8 (VOPT-70): finalize checks whether anything moved and resumes, so the run leaves these jobs alone.
        foreach (var job in preparation.InterruptedFinalizations) observer.FinalisationInterrupted(job);
        if (preparation.Held > 0) observer.VideosHeld(preparation.Held);
        preparation = await ReadyImmichVideosAsync(settings, observer, preparation, cancellationToken);
        if (preparation.RunnableJobs.Count == 0)
        {
            await SummariseAsync(settings, observer, 0, 0, [.. preparation.FailedJobs], stoppedAfter: false, toCheck: 0);
            return (0, 0, [.. preparation.FailedJobs], false, false, true);
        }

        var managed = lifecycle.IsManaged(settings);
        // A server bills from the moment it exists, so it is created only once a job will reach a remote stage (VOPT-68).
        var managedWork = managed && await AnyJobNeedsWorkerAsync(settings, preparation.RunnableJobs, cancellationToken);
        if (managedWork)
        {
            // Not reset in a finally: a stop leaves the flag set for the events that follow it.
            _settingUpWorker = true;
            var worker = await lifecycle.EnsureReadyAsync(settings, settings.Database.Path, new InlineProgress<string>(observer.WorkerProgress), questions.ConfirmWorkerCreationAsync, cancellationToken);
            _settingUpWorker = false;
            _used = true;
            // Only a managed worker reaches here, so a run on this PC or a manual host never names a worker. On no token: the
            // worker bills from now whether or not a stop comes next.
            _workerServerId = worker.ServerId;
            if (_runId is { } runId) await records.SetRunWorkerAsync(settings.Database.Path, runId, worker.ServerId, CancellationToken.None);
        }
        // Asks the worker question at the first stop request only when a managed worker is up; read from the local state file.
        var workerUp = managed && await lifecycle.ReadRecordedAsync(settings.Database.Path, cancellationToken) is not null;
        var progress = observer.ConversionStarted(preparation.RunnableJobs.Count, workerUp);
        var joinFailures = new List<FailedQueueJob>();
        QueueRunResult result;
        try
        {
            var passOver = PassOverJoiningVideos(settings, modeOverridden, observer, preparation, workerEnsured: managedWork, joinFailures);
            // The worker is ensured before the first video or not at all (a joining video that needs it waits), so it is the
            // same for every span of this conversion.
            var spans = _runId is { } runId ? new ConversionSpans(records, time, settings.Database.Path, runId, _workerServerId) : null;
            result = await queue.RunAsync(settings.Database.Path, settings, preparation, passOver, progress, spans, stopAfterThisVideo, cancellationToken);
        }
        finally
        {
            observer.ConversionEnded();
        }

        var failures = preparation.FailedJobs.Concat(joinFailures).Concat(result.FailedJobs).ToArray();
        if (result.UnreachableWorkerJob is not null) observer.StoppedByUnreachableWorker(await WorkerNameAsync(settings), result.NotStarted + 1);
        if (result.StoppedAfter is { } last) observer.StoppedAfter(last, result.NotStarted);
        await SummariseAsync(settings, observer, result.ReadyToFinalize, result.NotWorthConverting, failures, stoppedAfter: result.StoppedAfter is not null, toCheck: result.ToCheck);
        var end = result.UnreachableWorkerJob is not null ? RunEnd.WorkerUnreachable : result.StoppedAfter is not null ? RunEnd.StoppedAfterVideo : RunEnd.Finished;
        var workerHandled = await ConcludeAsync(settings, observer, questions, end, unreachableJob: result.UnreachableWorkerJob);
        // U3m (VOPT-69): a manual host that stopped answering is neither billed nor deleted by this tool, so the user hears only how to resume.
        if (result.UnreachableWorkerJob is not null)
        {
            if (!managed) observer.ManualWorkerUnreachable(result.UnreachableWorkerJob);
        }
        else if (!managed && settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase))
        {
            observer.ManualWorkerReminder();
        }
        return (result.ReadyToFinalize, result.NotWorthConverting, failures, result.UnreachableWorkerJob is not null, result.StoppedAfter is not null, workerHandled);
    }

    private async Task SummariseAsync(AppSettings settings, IRunObserver observer, int ready, int skipped, IReadOnlyList<FailedQueueJob> failures, bool stoppedAfter, int toCheck)
    {
        // The videos earlier runs left ready count too (VOPT-73 B5): saying "Nothing to finalise." with some waiting is wrong.
        // Read without the run's token, which a second Ctrl+C has cancelled by now, as the worker's name is.
        // Never fewer than this run made, whatever the read says, so a video it just readied is always named.
        var readyTotal = Math.Max(ready, (await discovery.OpenJobsAsync(settings.Database.Path, CancellationToken.None)).Count(entry => entry.Group == OpenJobGroup.ReadyToFinalise));
        observer.Summary(ready, skipped, failures, readyTotal, stoppedAfter, toCheck);
    }

    /// <summary>
    /// R8: after preparation released the finalise lock and before the worker check, the Immich videos either get their
    /// downloaded copies back or stay queued, so the worker is created only for work that can run.
    /// Immich is asked only for a missing copy: converting needs nothing else from it. Folder videos convert either way.
    /// </summary>
    private async Task<QueuePreparationResult> ReadyImmichVideosAsync(AppSettings settings, IRunObserver observer, QueuePreparationResult preparation, CancellationToken cancellationToken)
    {
        var immichJobs = preparation.RunnableJobs.Where(job => job.SourceKind == JobSourceKind.Immich).ToArray();
        var foldersOnly = preparation with { RunnableJobs = preparation.RunnableJobs.Where(job => job.SourceKind == JobSourceKind.File).ToArray(), SourceKind = JobSourceKind.File };
        // Without Immich the run takes no Immich video, a joining one included.
        if (!settings.Immich.IsConfigured)
        {
            if (immichJobs.Length > 0) observer.ImmichVideosStayQueued(immichJobs.Length, unusableReason: null);
            return foldersOnly;
        }
        if (immichJobs.Length == 0) return preparation;

        var restage = await RestageAsync(settings, observer, immichJobs, cancellationToken);
        var failed = restage.Failed.Select(job => job.Id).ToHashSet();
        var ready = restage.Unusable is null ? preparation : foldersOnly;
        return ready with
        {
            RunnableJobs = ready.RunnableJobs.Where(job => !failed.Contains(job.Id)).ToArray(),
            FailedJobs = [.. preparation.FailedJobs, .. restage.Failed.Select(job => ToFailedQueueJob(job, job.FailureMessage!))]
        };
    }

    /// <summary>
    /// Downloads again the missing copies of <paramref name="jobs"/>. When Immich can't be used, says how many of them stay
    /// queued: those it did not fail before it stopped answering.
    /// </summary>
    private async Task<ImmichRestageResult> RestageAsync(AppSettings settings, IRunObserver observer, JobRecord[] jobs, CancellationToken cancellationToken)
    {
        var restage = await restaging.RestageMissingAsync(settings.Database.Path, settings, jobs, observer.ImmichRestaging(), cancellationToken);
        if (restage.Unusable is { } reason) observer.ImmichVideosStayQueued(jobs.Length - restage.Failed.Count, reason);
        return restage;
    }

    private static FailedQueueJob ToFailedQueueJob(JobRecord job, string message) => new(job.Id, job.SourcePath, job.FailureCategory ?? "ProcessingFailed", job.FailureMessage ?? message);

    /// <summary>In run order, stopping at the first job that needs the worker: usually only the first job is probed twice.</summary>
    private async Task<bool> AnyJobNeedsWorkerAsync(AppSettings settings, IReadOnlyList<JobRecord> jobs, CancellationToken cancellationToken)
    {
        foreach (var job in jobs)
        {
            if (await processor.NeedsWorkerAsync(job, settings, cancellationToken)) return true;
        }
        return false;
    }

    /// <summary>
    /// For a video that joins the run after it prepared (queued, released or moved next from another terminal), the checks
    /// preparation made for the rest. Under <c>--local</c> or <c>--remote</c>, one started in the other mode is skipped, as
    /// preparation skips it. A managed run that created no worker cannot start one that needs it: it would stop at its
    /// first remote stage with nothing to connect to, so it waits for the next run. An Immich video whose downloaded copy is
    /// gone gets it back first, as preparation's do: one Immich no longer has fails into <paramref name="failures"/>, and one
    /// Immich can't be asked about stays queued.
    /// </summary>
    private Func<JobRecord, CancellationToken, Task<bool>> PassOverJoiningVideos(AppSettings settings, bool modeOverridden, IRunObserver observer, QueuePreparationResult preparation, bool workerEnsured, List<FailedQueueJob> failures)
    {
        var prepared = preparation.RunnableJobs.Select(job => job.Id).ToHashSet();
        var needsCheck = !workerEnsured && lifecycle.IsManaged(settings);
        return async (job, cancellationToken) =>
        {
            if (prepared.Contains(job.Id)) return false;
            if (modeOverridden && JobCompatibility.IsBoundToOtherMode(job, JobCompatibility.Evaluate(job, settings)))
            {
                observer.JoiningVideoSkipped(job);
                return true;
            }
            if (job.SourceKind == JobSourceKind.Immich)
            {
                var restage = await RestageAsync(settings, observer, [job], cancellationToken);
                failures.AddRange(restage.Failed.Select(failed => ToFailedQueueJob(failed, failed.FailureMessage!)));
                if (restage.Failed.Count > 0 || restage.Unusable is not null) return true;
            }
            if (!needsCheck || !await processor.NeedsWorkerAsync(job, settings, cancellationToken)) return false;
            observer.JoiningVideoWaitsForWorker(job);
            return true;
        };
    }

    /// <summary>The managed server's name, or the SSH host of a manual one. Read without the run's token, which may be cancelled.</summary>
    private async Task<string> WorkerNameAsync(AppSettings settings)
    {
        // Not the settings host for a managed worker: connecting replaced it with the server's IP address.
        var managedName = lifecycle.IsManaged(settings) ? (await lifecycle.ReadRecordedAsync(settings.Database.Path, CancellationToken.None))?.Name : null;
        return managedName ?? settings.Processing.RemoteSsh.Host;
    }

    /// <summary>
    /// An error while deciding the worker's fate (an unreadable state file, a busy database) is reported, and the run keeps
    /// the exit code of how it ended: a stop stays 9.
    /// </summary>
    private static async Task ConcludeOrReportAsync(IRunObserver observer, Func<Task<bool>> conclude)
    {
        try
        {
            _ = await conclude();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = observer.RunFailed(exception);
        }
    }

    /// <summary>Decides the managed worker's fate as the run ends and tells the observer what happened to it.</summary>
    /// <param name="interrupted">For <see cref="RunEnd.StoppedNow"/>, the video it stopped, or null when none was converting.</param>
    /// <param name="unreachableJob">For <see cref="RunEnd.WorkerUnreachable"/>, the video the worker left interrupted.</param>
    /// <returns>False when deleting the worker failed.</returns>
    private async Task<bool> ConcludeAsync(AppSettings settings, IRunObserver observer, IRunQuestions questions, RunEnd end, JobInterruptedException? interrupted = null, JobRecord? unreachableJob = null)
    {
        _concluded = true;
        var answer = await questions.AnswerGivenDuringRunAsync();
        var recorded = lifecycle.IsManaged(settings) ? await lifecycle.ReadRecordedAsync(settings.Database.Path, CancellationToken.None) : null;
        if (recorded is null)
        {
            if (end == RunEnd.StoppedNow)
            {
                var manualHost = settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase) && !lifecycle.IsManaged(settings)
                    ? settings.Processing.RemoteSsh.Host
                    : null;
                observer.StoppedWithoutManagedWorker(interrupted, manualHost);
            }
            return true;
        }

        var worker = recorded.Name;
        JobStatus? remoteStage = end switch
        {
            RunEnd.StoppedNow => interrupted is { Remote: true } ? interrupted.Stage : null,
            RunEnd.WorkerUnreachable => unreachableJob?.ResumeStatus,
            _ => null
        };
        var fate = answer.Pending
            ? WorkerFate.Ask
            : WorkerEndPolicy.Decide(end, answer.Answer, settings.Processing.RemoteSsh.Hetzner.DeleteAfterRun, _used, WorkerEndPolicy.DeleteLosesWork(remoteStage), questions.CanAsk);
        var stopLike = end is RunEnd.StoppedNow or RunEnd.WorkerUnreachable;

        if (end == RunEnd.StoppedNow) observer.StoppedNow(interrupted, worker, fate, _settingUpWorker);
        else if (end == RunEnd.WorkerUnreachable && fate != WorkerFate.Delete) observer.WorkerUnreachableAtEnd(unreachableJob!, questionFollows: fate == WorkerFate.Ask);

        if (fate == WorkerFate.Ask)
        {
            if (!await questions.AskToDeleteWorkerAsync(end, remoteStage))
            {
                observer.WorkerKept(worker, end, remoteStage, answeredNo: true);
                return true;
            }
            fate = WorkerFate.Delete;
        }

        if (fate == WorkerFate.Keep)
        {
            // A stop's events and U3 already say the worker stays and how to delete it.
            if (!stopLike) observer.WorkerKept(worker, end, remoteStage, answeredNo: false);
            return true;
        }
        return await DeleteWorkerAsync(settings, observer, worker);
    }

    /// <returns>False when the delete failed; the observer then says the worker still bills.</returns>
    private async Task<bool> DeleteWorkerAsync(AppSettings settings, IRunObserver observer, string worker)
    {
        observer.WorkerDeleting();
        try
        {
            await lifecycle.DeleteAsync(settings, settings.Database.Path, new InlineProgress<string>(observer.WorkerProgress), CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            observer.WorkerDeleteFailed(worker, exception.GetBaseException().Message);
            return false;
        }
        finally
        {
            observer.WorkerDeleteEnded();
        }
    }

    // Progress<T> posts to a thread-pool thread when there is no synchronisation context, which would reorder lines the
    // run prints around them.
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
