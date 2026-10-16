using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Cli.Features.Finalize;
using VideoOptimiser.Cli.Features.Queue;
using VideoOptimiser.Cli.Features.Run;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// What each effect does: calls the Application use case the CLI's own command calls, and words the outcome for the log and the
/// footer. The wording keeps the meaning of the CLI's lines for the same outcomes (<c>QueueCommands</c>, <c>FinalizeCommands</c>)
/// and shortens them: the screen has no command to type, so a line that names one says what to press or what the next run does.
/// Changed wording goes through the humanizer gate (docs index). One per command; it holds no state of its own.
/// </summary>
internal sealed class TuiActions(
    IQueueVerbs verbs,
    IJobRepository jobs,
    IJobRetryService retry,
    IRemoteWorkerLifecycle lifecycle,
    BackgroundLaunch launch,
    StopRunRequest stop,
    IRunRequestStore requests,
    IFinalizationService finalizer,
    IDatabaseLocks locks,
    IFileOpener opener)
{
    private static ActionResult Done(LogKind kind, string text, bool refreshWorker = false) => ActionResult.Say(kind, text, refreshWorker);

    private static string OneLine(Exception exception) => RunEventWords.Clean(exception.GetBaseException().Message.Split('\n', 2)[0]);

    private static string FileName(JobRecord job) => Path.GetFileName(job.SourcePath);

    // ---- queue ------------------------------------------------------------------------------

    public async Task<ActionResult> QueueAsync(QueueActionEffect effect, AppSettings settings, CancellationToken cancellationToken)
    {
        var database = settings.Database.Path;
        if (effect.Action == QueueAction.Retry) return await RetryAsync(effect, settings, cancellationToken);

        // Read again: the verbs write only while the job is as they read it, so a copy from before another command changed it fails.
        var job = await jobs.GetAsync(database, effect.Id, cancellationToken);
        if (job is null) return Done(LogKind.Warning, $"{effect.Name} is not in the queue any more.");

        var outcome = effect.Action switch
        {
            QueueAction.Hold => await verbs.HoldAsync(database, job, cancellationToken),
            QueueAction.Release => await verbs.ReleaseAsync(database, job, cancellationToken),
            QueueAction.Next => await verbs.NextAsync(database, job, cancellationToken),
            QueueAction.Reject => await verbs.RejectAsync(settings, job, cancellationToken),
            _ => await verbs.DropAsync(settings, job, cancellationToken)
        };
        return await WordAsync(outcome, settings, rejecting: effect.Action == QueueAction.Reject, cancellationToken);
    }

    private async Task<ActionResult> WordAsync(QueueVerbOutcome outcome, AppSettings settings, bool rejecting, CancellationToken cancellationToken)
    {
        var job = outcome.Job;
        var name = FileName(job);
        switch (outcome)
        {
            case JobHeld:
                return Done(LogKind.Action, $"Held {name}. Runs pass over it until it's released.");
            case JobAlreadyHeld:
                return Done(LogKind.Info, $"{name} is already held.");
            case JobReleased:
                return Done(LogKind.Action, $"Released {name}; runs take it again.");
            case JobNotHeld:
                return Done(LogKind.Info, $"{name} isn't held.");
            case JobMovedNext moved:
                return Done(LogKind.Action, $"{name} {(moved.RunTakesIt ? "runs next" : "is now first in the queue")}{(moved.WasHeld ? "; it's no longer held" : string.Empty)}.");
            case VerbRefused refused:
                return Done(LogKind.Warning, Refusal(refused, locks.LiveRun(settings.Database.Path)));
            case JobAlreadyEnded:
                var cleanup = await verbs.CleanupDeletesFilesAsync(settings, job, cancellationToken) ? " Cleanup deletes its temporary files." : string.Empty;
                return Done(LogKind.Warning, $"{name} {QueueWords.StatusPhrase(job)}, so there is nothing to {(rejecting ? "reject" : "drop")}.{cleanup}");
            case FinaliseRunning:
                return Done(LogKind.Warning, $"A finalise is running on this database. Wait for it to finish, then {(rejecting ? "reject" : "drop")} {name}.");
            case OnlyCopyAtRisk onlyCopy:
                return Done(LogKind.Warning, OnlyCopy(onlyCopy, rejecting ? "reject" : "drop"));
            case JobDropped dropped:
                var lines = new List<LogLine> { new(LogKind.Action, $"Dropped {name}.") };
                if (dropped.RemoteStage is { } remote) lines.Add(new LogLine(LogKind.Info, $"Its {(remote.Stage == RemoteStage.Encode ? "encode" : "CRF search")} may still be running on the worker; the next run on that worker stops it."));
                if (await verbs.LeftFilesAsync(settings, dropped.Job, dropped.HadFinishedAv1, cancellationToken) is { } files) lines.Add(new LogLine(LogKind.Info, LeftFiles(files)));
                if (dropped.UploadedAssetId is { } uploaded) lines.Add(new LogLine(LogKind.Warning, $"The AV1 already uploaded to Immich as asset {uploaded} stays there; delete it in Immich if you do not want it."));
                return new ActionResult(lines, $"Dropped {name}.");
            case JobRejected rejected:
                var rejectLines = new List<LogLine> { new(LogKind.Action, $"Rejected {name}. The original{(job.SourceKind == JobSourceKind.Immich ? " in Immich" : string.Empty)} is untouched.") };
                if (rejected.Deleted.Any) rejectLines.Add(new LogLine(LogKind.Info, $"Deleted its {QueueWords.FilesPhrase(rejected.Deleted)}."));
                if (rejected.LeftForCleanup is { } left)
                {
                    var both = left is { Av1: true, DownloadedCopy: true };
                    rejectLines.Add(new LogLine(LogKind.Warning, $"Couldn't delete its {QueueWords.FilesPhrase(left)}; {(both ? "they may be" : "it may be")} open in another program. {(both ? "They stay" : "It stays")} until cleanup deletes {(both ? "them" : "it")}."));
                }
                return new ActionResult(rejectLines, $"Rejected {name}.");
            default:
                throw new InvalidOperationException($"A queue verb cannot end as {outcome.GetType().Name}.");
        }
    }

    private static string Refusal(VerbRefused refused, LiveRunHolder? run)
    {
        var job = refused.Job;
        var name = FileName(job);
        return refused.Reason switch
        {
            VerbRefusal.ConvertingNow => $"{name} is converting now ({QueueWords.StatusWord(job.Status)}). {StopWords.HowToStop(run)}",
            VerbRefusal.StoppedWhileFinalising => $"{name} was stopped while being finalised; finalising it checks and resumes it.",
            VerbRefusal.BeingFinalised => $"{name} is being finalised now.",
            VerbRefusal.NotReady => $"Only a video that is ready to finalise can be rejected; {name} {QueueWords.StatusPhrase(job)}.",
            _ => $"Only waiting and stopped videos can be held or run next; {name} {QueueWords.StatusPhrase(job)}."
        };
    }

    /// <summary>The refusals that protect a video whose AV1 may be the only copy keep their meaning and their paths.</summary>
    private static string OnlyCopy(OnlyCopyAtRisk onlyCopy, string verb)
    {
        var job = onlyCopy.Job;
        var name = FileName(job);
        return onlyCopy.Reason switch
        {
            OnlyCopyReason.OriginalMissing => $"The original of {name} is missing, so its AV1 at {job.OutputPath} may be the only copy. Put the original back, or move the AV1 somewhere safe, then {verb} the video.",
            OnlyCopyReason.ImmichNotConfigured => $"Immich can't be used (immich.baseUrl isn't set), so the original of {name} can't be checked. Try again once it answers.",
            OnlyCopyReason.ImmichUnavailable => $"Immich can't be used ({onlyCopy.Detail?.TrimEnd('.')}), so the original of {name} can't be checked. Try again once it answers.",
            _ => $"The original of {name} is gone from Immich, so its AV1 and downloaded copy may be the only copies: {job.OutputPath}, {job.SourcePath}. Move them somewhere safe, then {verb} the video."
        };
    }

    private static string LeftFiles(LeftFiles files)
    {
        var av1 = files.Av1 switch
        {
            LeftAv1.Finished => "AV1",
            LeftAv1.Unfinished => "unfinished AV1",
            _ => null
        };
        return (av1, files.DownloadedCopy) switch
        {
            (null, _) => "Its downloaded copy stays until cleanup deletes it.",
            (_, false) => $"Its {av1} stays until cleanup deletes it.",
            _ => $"Its {av1} and downloaded copy stay until cleanup deletes them."
        };
    }

    private async Task<ActionResult> RetryAsync(QueueActionEffect effect, AppSettings settings, CancellationToken cancellationToken)
    {
        var result = await retry.RetryAsync(settings.Database.Path, effect.Id, settings, cancellationToken);
        if (!result.Succeeded) return Done(LogKind.Error, result.Error ?? $"Couldn't queue {effect.Name} again.");
        var lines = new List<LogLine> { new(LogKind.Action, $"Queued {FileName(result.Replacement!)} again as job {result.Replacement!.Id:N}.") };
        if (result.DownloadedCopyGone) lines.Add(new LogLine(LogKind.Info, "Its downloaded copy is gone; the next run downloads it again first."));
        return new ActionResult(lines, $"Queued {FileName(result.Replacement)} again.");
    }

    // ---- worker -----------------------------------------------------------------------------

    public async Task<WorkerOptionsMsg> LoadWorkerOptionsAsync(WorkerOptionsPurpose purpose, AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            return new WorkerOptionsMsg(purpose, await lifecycle.ListCreationOptionsAsync(settings, cancellationToken), null);
        }
        catch (RemoteWorkerCapacityUnavailableException exception)
        {
            return new WorkerOptionsMsg(purpose, null, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new WorkerOptionsMsg(purpose, null, OneLine(exception));
        }
    }

    public async Task<ActionResult> CreateWorkerAsync(RemoteWorkerCreationOption option, AppSettings settings, Action<LogKind, string> log, CancellationToken cancellationToken)
    {
        try
        {
            var worker = await lifecycle.CreateAsync(settings, settings.Database.Path, option, new LogProgress(log), cancellationToken);
            return Done(LogKind.Success, worker.Status == RemoteWorkerStatuses.Bootstrapping
                ? $"Created the worker {worker.Name} ({worker.ServerType} in {worker.Location}). It is bootstrapping, and bills hourly until it is deleted."
                : $"The worker {worker.Name} is up.", refreshWorker: true);
        }
        catch (RemoteWorkerCapacityUnavailableException exception)
        {
            return CapacityFailure(exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Done(LogKind.Error, $"Couldn't create the worker: {OneLine(exception)}", refreshWorker: true);
        }
    }

    /// <summary>Hetzner's own message lists what it tried per type; each line goes in the log, the first in the footer.</summary>
    private static ActionResult CapacityFailure(RemoteWorkerCapacityUnavailableException exception)
    {
        var lines = exception.Message.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        return new ActionResult([.. lines.Select(line => new LogLine(LogKind.Error, line))], lines[0], RefreshWorker: true);
    }

    public async Task<ActionResult> DeleteWorkerAsync(AppSettings settings, Action<LogKind, string> log, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await lifecycle.DeleteAsync(settings, settings.Database.Path, new LogProgress(log), cancellationToken);
            // The lifecycle's own lines say what was deleted and that billing has stopped.
            return deleted ? new ActionResult([], "Deleted the worker.", RefreshWorker: true) : Done(LogKind.Info, "There was no worker to delete.", refreshWorker: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ActionResult(
                [new LogLine(LogKind.Error, $"Couldn't delete the worker: {OneLine(exception)}"), new LogLine(LogKind.Warning, "If it is still up, it bills hourly until it is deleted.")],
                "Couldn't delete the worker.", RefreshWorker: true);
        }
    }

    // ---- a run ------------------------------------------------------------------------------

    /// <summary>
    /// Creates the chosen server first, when there is one, then launches the detached runner with what the dialogs answered. A
    /// failed create launches nothing. The runner asks nothing: the worker and its fate are settled here.
    /// </summary>
    public async Task<ActionResult> StartRunAsync(StartPlan plan, AppSettings settings, string configurationPath, Action<LogKind, string> log, CancellationToken cancellationToken)
    {
        var database = settings.Database.Path;
        RemoteWorkerInfo? created = null;
        if (plan.Create is { } option)
        {
            try
            {
                created = await lifecycle.CreateAsync(settings, database, option, new LogProgress(log), cancellationToken);
            }
            catch (RemoteWorkerCapacityUnavailableException exception)
            {
                var failure = CapacityFailure(exception);
                return failure with { Lines = [.. failure.Lines, new LogLine(LogKind.Warning, "No run was started.")] };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new ActionResult([new LogLine(LogKind.Error, $"Couldn't create the worker: {OneLine(exception)}"), new LogLine(LogKind.Warning, "No run was started.")], "Couldn't create the worker.", RefreshWorker: true);
            }
            log(LogKind.Action, $"Created the worker {created.Name} ({created.ServerType} in {created.Location}). It is bootstrapping.");
        }

        BackgroundLaunchResult result;
        try
        {
            result = await launch.StartAsync(database, BackgroundRunnerArguments.For(configurationPath, plan.Fate, plan.Mode), Environment.CurrentDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return WithWorkerWarning(new ActionResult([new LogLine(LogKind.Error, $"Couldn't start the run: {OneLine(exception)}")], "Couldn't start the run.", created is not null), created);
        }

        var started = result.Outcome switch
        {
            BackgroundLaunchOutcome.Started => Done(LogKind.Success, $"Started a background run (process {result.ProcessId?.ToString(CultureInfo.InvariantCulture)}). Closing this screen doesn't stop it.", created is not null),
            BackgroundLaunchOutcome.AlreadyRunning => Done(LogKind.Warning, result.Holder is { } holder
                ? $"A run is already going: started {CliLocks.FormatStarted(holder.StartedUtc)} (process {holder.ProcessId.ToString(CultureInfo.InvariantCulture)})."
                : "A run is already going.", created is not null),
            BackgroundLaunchOutcome.ExitedEarly => Done(LogKind.Error, $"The runner ended at once{(result.ExitCode is { } code ? $" with exit code {code.ToString(CultureInfo.InvariantCulture)}" : string.Empty)}, so no run started.", created is not null),
            _ => Done(LogKind.Warning, $"The runner started (process {result.ProcessId?.ToString(CultureInfo.InvariantCulture)}) but hasn't taken the run lock yet. Check the Activity pane in a moment.", created is not null)
        };
        return result.Outcome == BackgroundLaunchOutcome.Started ? started : WithWorkerWarning(started, created);
    }

    /// <summary>A server made for a run that never started bills until someone deletes it.</summary>
    private static ActionResult WithWorkerWarning(ActionResult result, RemoteWorkerInfo? created) => created is null
        ? result
        : result with { Lines = [.. result.Lines, new LogLine(LogKind.Warning, $"The worker {created.Name} is up and bills hourly until it is deleted: delete it in the Worker pane, or start the run again to use it.")] };

    public async Task<ActionResult> StopAsync(bool now, AppSettings settings, CancellationToken cancellationToken)
    {
        var result = await stop.StopAsync(settings.Database.Path, now, cancellationToken);
        return result.Outcome switch
        {
            StopRunOutcome.NoRun => Done(LogKind.Warning, "No run is going."),
            StopRunOutcome.Foreground => Done(LogKind.Warning, $"That run is in another window ({result.Holder?.Command}); only Ctrl+C there stops it."),
            _ => now
                ? Done(LogKind.Warning, "Asked the run to stop now. The video converting now starts again from its stage next time.")
                : Done(LogKind.Action, "Asked the run to stop after this video.")
        };
    }

    public async Task<ActionResult> SetWorkerFateAsync(WorkerFateChoice fate, AppSettings settings, CancellationToken cancellationToken)
    {
        await requests.SetWorkerFateAsync(settings.Database.Path, fate, cancellationToken);
        return Done(LogKind.Action, fate == WorkerFateChoice.Delete
            ? "The worker will be deleted when the run ends."
            : "The worker will stay up when the run ends, billed hourly.");
    }

    // ---- review -----------------------------------------------------------------------------

    public async Task<ActionResult> OpenAsync(OpenFile effect, AppSettings settings, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(settings.Database.Path, effect.Id, cancellationToken);
        if (job is null) return Done(LogKind.Warning, $"{effect.Name} is not in the queue any more.");
        var path = effect.Original ? job.SourcePath : job.OutputPath;
        var what = effect.Original ? (job.SourceKind == JobSourceKind.Immich ? "downloaded copy" : "original") : "AV1";
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Done(LogKind.Error, $"The {what} of {effect.Name} is missing{(string.IsNullOrEmpty(path) ? "." : $": {path}")}");
        try
        {
            opener.Open(path);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Done(LogKind.Error, $"Couldn't open the {what} of {effect.Name}: {OneLine(exception)}");
        }
        return new ActionResult([], $"Opened the {what} of {effect.Name}.");
    }

    /// <summary>The same rule as <c>finalize --ready</c>: ready videos, and the ones whose finalisation was interrupted.</summary>
    private static bool IsFinalisable(JobRecord job) =>
        job.Status == JobStatus.ReadyToFinalize || FinalizationRules.IsInterruptedImmich(job) || FinalizationRules.IsInterruptedFile(job);

    /// <summary>The confirmation <c>finalize</c> shows: a line per video, what happens to the originals, and the Immich line.</summary>
    public async Task<PreparedMsg> PrepareFinaliseAsync(PrepareFinalise effect, AppSettings settings, CancellationToken cancellationToken)
    {
        var open = await jobs.ListAsync(settings.Database.Path, terminal: false, cancellationToken);
        var ready = JobRunOrder.Sort(open).Where(IsFinalisable).ToArray();
        if (effect.Id is { } id) ready = [.. ready.Where(job => job.Id == id)];
        if (ready.Length == 0) return new PreparedMsg(effect.BusyKey!, null, effect.Id is null ? "Nothing is ready to finalise." : "That video can't be finalised now.");

        const int shown = 6;
        var lines = ready.Take(shown).Select(FinalizationConsole.DescribeReady).ToList();
        if (ready.Length > shown) lines.Add($"…and {ready.Length - shown} more.");
        if (ready.Where(job => job.SourceKind == JobSourceKind.File).ToArray() is { Length: > 0 } fileJobs) lines.Add(FinalizationConsole.DescribeOriginalAction(settings.Original, fileJobs));
        if (ready.Any(job => job.SourceKind == JobSourceKind.Immich)) lines.Add("Each Immich original goes to Immich's trash once its AV1 is uploaded with its albums, tags and other relations.");

        var question = ready.Length == 1 ? $"Finalise {FileName(ready[0])}?" : $"Finalise {ready.Length.ToString(CultureInfo.InvariantCulture)} videos?";
        return new PreparedMsg(effect.BusyKey!, new TuiConfirm(question, string.Join('\n', lines), new FinaliseVideos([.. ready.Select(job => job.Id)])), null);
    }

    public async Task<ActionResult> FinaliseAsync(FinaliseVideos effect, AppSettings settings, CancellationToken cancellationToken)
    {
        var database = settings.Database.Path;
        // The lock a finalise holds, so a run leaves these videos alone while they are finalised.
        using var finaliseLock = await locks.TakeFinalizeAsync(database, "finalize", cancellationToken);
        if (finaliseLock is null) return Done(LogKind.Warning, "Another finalise is running on this database, so nothing was finalised. Wait for it to finish.");

        var lines = new List<LogLine>();
        var failed = 0;
        foreach (var id in effect.Ids)
        {
            var name = id.ToString("N");
            try
            {
                name = (await jobs.GetAsync(database, id, cancellationToken)) is { } job ? FileName(job) : name;
                var result = await finalizer.FinalizeAsync(database, id, settings, cancellationToken);
                var succeeded = result.ExitCode == ExitCode.Success;
                if (!succeeded) failed++;
                lines.Add(new LogLine(succeeded ? LogKind.Success : LogKind.Error, RunEventWords.Clean($"{(succeeded ? "Finalised" : "Couldn't finalise")} {name}: {result.Message}")));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                lines.Add(new LogLine(LogKind.Error, $"Couldn't finalise {name}: {OneLine(exception)}"));
            }
        }

        var finalised = effect.Ids.Count - failed;
        var summary = failed == 0
            ? finalised == 1 ? "Finalised 1 video." : $"Finalised {finalised.ToString(CultureInfo.InvariantCulture)} videos."
            : $"Finalised {finalised.ToString(CultureInfo.InvariantCulture)}; {failed.ToString(CultureInfo.InvariantCulture)} failed.";
        if (effect.Ids.Count > 1) lines.Add(new LogLine(failed == 0 ? LogKind.Success : LogKind.Warning, summary));
        return new ActionResult(lines, summary);
    }

    /// <summary>Reports progress lines as they come, on the thread that raised them: a created worker's bootstrap takes minutes.</summary>
    private sealed class LogProgress(Action<LogKind, string> log) : IProgress<string>
    {
        public void Report(string value) => log(LogKind.Info, RunEventWords.Clean(value));
    }
}
