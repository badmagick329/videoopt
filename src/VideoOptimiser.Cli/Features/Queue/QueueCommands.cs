using System.Text.Json;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using static CliOutput;
using static VideoOptimiser.Cli.Features.Queue.QueueWords;

namespace VideoOptimiser.Cli.Features.Queue;

/// <summary>
/// The queue commands: the verbs <c>queue hold</c>, <c>release</c>, <c>next</c>, <c>drop</c> (VOPT-54 step 5), <c>reject</c> (VOPT-112) and <c>dismiss</c> (VOPT-115); the
/// commands that put videos in the queue, <c>queue add</c> (was <c>queue discover</c>), <c>queue add &lt;file&gt;</c> and
/// <c>queue retry</c> (<c>queue add immich</c> is the Immich slice's), each ending with what to run next; and the job lists <c>queue list</c> and
/// <c>history</c>, as lines or <c>--json</c>. When a verb acts and when it refuses is decided by <see cref="IQueueVerbs"/>
/// and the discovery and retry services; this holds the wording and the stream each line goes to. Changed wording goes
/// through the humanizer gate (docs index).
/// </summary>
internal sealed class QueueCommands(IQueueVerbs verbs, IJobRepository repository, IQueueDiscovery discovery, IJobRetryService retryService)
{
    public async Task<int> HoldAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (await FindJobAsync(configuration, command, cancellationToken) is not { } job) return (int)ExitCode.InvalidArguments;
        var outcome = await verbs.HoldAsync(configuration.Settings.Database.Path, job, cancellationToken);
        var name = Path.GetFileName(outcome.Job.SourcePath);
        switch (outcome)
        {
            case VerbRefused refused:
                return Refuse(RefusalText(refused, command, verb: "hold", notWaitingAction: "be held"));
            case JobAlreadyHeld:
                CliConsole.WriteLine($"{name} is already held.");
                return (int)ExitCode.Success;
            default:
                CliConsole.WriteLine($"Held {name}. Runs pass over it until '{command.Hint($"queue release {outcome.Job.Id:N}")}'.");
                return (int)ExitCode.Success;
        }
    }

    public async Task<int> ReleaseAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (await FindJobAsync(configuration, command, cancellationToken) is not { } job) return (int)ExitCode.InvalidArguments;
        var outcome = await verbs.ReleaseAsync(configuration.Settings.Database.Path, job, cancellationToken);
        var name = Path.GetFileName(outcome.Job.SourcePath);
        CliConsole.WriteLine(outcome is JobNotHeld ? $"{name} isn't held." : $"Released {name}; runs take it again.");
        return (int)ExitCode.Success;
    }

    public async Task<int> NextAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (await FindJobAsync(configuration, command, cancellationToken) is not { } job) return (int)ExitCode.InvalidArguments;
        var outcome = await verbs.NextAsync(configuration.Settings.Database.Path, job, cancellationToken);
        var name = Path.GetFileName(outcome.Job.SourcePath);
        switch (outcome)
        {
            case VerbRefused { Reason: VerbRefusal.ConvertingNow }:
                CliConsole.WriteLine($"{name} is converting now.");
                return (int)ExitCode.Success;
            case VerbRefused refused:
                return Refuse(RefusalText(refused, command, verb: "run next", notWaitingAction: "run next"));
            case JobMovedNext moved:
                CliConsole.WriteLine($"{name} {(moved.RunTakesIt ? "runs next" : "is now first in the queue")}{(moved.WasHeld ? "; it's no longer held" : string.Empty)}.");
                return (int)ExitCode.Success;
            default:
                throw new InvalidOperationException($"queue next cannot end as {outcome.GetType().Name}.");
        }
    }

    public async Task<int> DropAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (command.All) return await DropAllAsync(configuration, command, cancellationToken);
        if (await FindJobAsync(configuration, command, cancellationToken) is not { } job) return (int)ExitCode.InvalidArguments;
        var outcome = await verbs.DropAsync(configuration.Settings, job, cancellationToken);
        var name = Path.GetFileName(outcome.Job.SourcePath);
        switch (outcome)
        {
            case VerbRefused refused:
                return Refuse(RefusalText(refused, command, verb: "drop", notWaitingAction: null));
            case JobAlreadyEnded ended:
                WriteError($"{name} {StatusPhrase(ended.Job)}, so there is nothing to drop.");
                if (await verbs.CleanupDeletesFilesAsync(configuration.Settings, ended.Job, cancellationToken)) WriteError($"'{command.Hint("cleanup")}' deletes its temporary files.");
                return (int)ExitCode.GeneralFailure;
            case FinaliseRunning:
                return Refuse($"A finalise is running on this database. Wait for it to finish, then drop {name}.");
            case OnlyCopyAtRisk onlyCopy:
                return Refuse(OnlyCopyText(onlyCopy, "drop"));
            case JobDropped dropped:
                CliConsole.WriteLine($"Dropped {name}.");
                if (dropped.RemoteStage is { } remote) CliConsole.WriteLine(RemoteStageLine(remote));
                if (await verbs.LeftFilesAsync(configuration.Settings, dropped.Job, dropped.HadFinishedAv1, cancellationToken) is { } files) CliConsole.WriteLine(LeftFilesLine(files, command));
                if (dropped.UploadedAssetId is { } uploaded) CliConsole.WriteLine($"The AV1 already uploaded to Immich as asset {uploaded} stays there; delete it in Immich if you do not want it.");
                return (int)ExitCode.Success;
            default:
                throw new InvalidOperationException($"queue drop cannot end as {outcome.GetType().Name}.");
        }
    }

    /// <summary>
    /// <c>queue reject &lt;job-id&gt;</c> (VOPT-112). It asks nothing, like <c>queue drop</c> of a ready video: the original
    /// stays, and <c>queue retry</c> makes the AV1 again.
    /// </summary>
    public async Task<int> RejectAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (await FindJobAsync(configuration, command, cancellationToken) is not { } job) return (int)ExitCode.InvalidArguments;
        var outcome = await verbs.RejectAsync(configuration.Settings, job, cancellationToken);
        var name = Path.GetFileName(outcome.Job.SourcePath);
        switch (outcome)
        {
            case VerbRefused refused:
                return Refuse(RefusalText(refused, command, verb: "reject", notWaitingAction: null));
            case JobAlreadyEnded ended:
                WriteError($"{name} {StatusPhrase(ended.Job)}, so there is nothing to reject.");
                if (await verbs.CleanupDeletesFilesAsync(configuration.Settings, ended.Job, cancellationToken)) WriteError($"'{command.Hint("cleanup")}' deletes its temporary files.");
                return (int)ExitCode.GeneralFailure;
            case FinaliseRunning:
                return Refuse($"A finalise is running on this database. Wait for it to finish, then reject {name}.");
            case OnlyCopyAtRisk onlyCopy:
                return Refuse(OnlyCopyText(onlyCopy, "reject"));
            case JobRejected rejected:
                var immich = rejected.Job.SourceKind == JobSourceKind.Immich;
                CliConsole.WriteLine($"Rejected {name}. The original{(immich ? " in Immich" : string.Empty)} is untouched.");
                if (rejected.Deleted.Any) CliConsole.WriteLine($"Deleted its {FilesPhrase(rejected.Deleted)}.");
                if (rejected.LeftForCleanup is { } left) CliConsole.WriteLine(LeftForCleanupLine(left, command));
                CliConsole.WriteLine($"To try it again: {command.Hint($"queue retry {rejected.Job.Id:N}")}");
                return (int)ExitCode.Success;
            default:
                throw new InvalidOperationException($"queue reject cannot end as {outcome.GetType().Name}.");
        }
    }

    /// <summary>
    /// <c>queue dismiss &lt;job-id&gt;</c> and <c>--all</c> (VOPT-115). It asks nothing: dismissing only hides a failure from the
    /// TUI's Failed group, and the job stays in <c>history</c> for <c>queue retry</c>.
    /// </summary>
    public async Task<int> DismissAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var database = configuration.Settings.Database.Path;
        if (command.All)
        {
            var count = await verbs.DismissAllAsync(database, cancellationToken);
            var history = command.Hint("history");
            CliConsole.WriteLine(count switch
            {
                0 => "No failures to dismiss.",
                1 => $"Dismissed 1 failure. Its original stays skipped; '{history}' still lists it.",
                _ => $"Dismissed {count} failures. Their originals stay skipped; '{history}' still lists them."
            });
            return (int)ExitCode.Success;
        }
        if (await FindJobAsync(configuration, command, cancellationToken) is not { } job) return (int)ExitCode.InvalidArguments;
        var outcome = await verbs.DismissAsync(database, job, cancellationToken);
        var name = Path.GetFileName(outcome.Job.SourcePath);
        switch (outcome)
        {
            case VerbRefused refused:
                return Refuse($"Only a failed video can be dismissed; {name} {StatusPhrase(refused.Job)}.");
            case JobAlreadyDismissed:
                CliConsole.WriteLine($"{name} is already dismissed.");
                return (int)ExitCode.Success;
            case JobDismissed:
                CliConsole.WriteLine($"Dismissed {name}. Its original stays skipped. To try it again: {command.Hint($"queue retry {job.Id:N}")}");
                return (int)ExitCode.Success;
            default:
                throw new InvalidOperationException($"queue dismiss cannot end as {outcome.GetType().Name}.");
        }
    }

    /// <summary>A file that cannot be deleted is almost always open in a player; the reject stands and cleanup finishes the job.</summary>
    private static string LeftForCleanupLine(RejectedFiles left, CliCommand command)
    {
        var both = left is { Av1: true, DownloadedCopy: true };
        return $"Couldn't delete its {FilesPhrase(left)}; {(both ? "they may be" : "it may be")} open in another program. {(both ? "They stay" : "It stays")} until '{command.Hint("cleanup")}' deletes {(both ? "them" : "it")}.";
    }

    /// <summary>Each step is printed as it happens, so a long drop shows its progress and a failure keeps what was already said.</summary>
    private async Task<int> DropAllAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var exitCode = ExitCode.Success;
        await foreach (var step in verbs.DropAllAsync(configuration.Settings.Database.Path, cancellationToken))
        {
            switch (step)
            {
                case NothingToDrop:
                    CliConsole.WriteLine("Nothing to drop: the queue is empty.");
                    return (int)ExitCode.Success;
                case ChangedUnderDropAll changed:
                    WriteError(new JobChangedException(changed.JobId).Message);
                    exitCode = ExitCode.GeneralFailure;
                    break;
                case DroppedInBulk dropped:
                    CliConsole.WriteLine($"Dropped {Path.GetFileName(dropped.Job.SourcePath)}.");
                    if (dropped.RemoteStage is { } remote) CliConsole.WriteLine(RemoteStageLine(remote));
                    break;
                case LeftStoppedFinalising left:
                    CliConsole.WriteLine($"Left 1 video stopped while being finalised; '{command.Hint($"finalize {left.Job.Id:N}")}' checks and resumes it.");
                    break;
                case LeftStoppedChecking left:
                    CliConsole.WriteLine($"Left 1 video stopped while its AV1 was checked; to drop it: {command.Hint($"queue drop {left.Job.Id:N}")}");
                    break;
            }
        }
        return (int)exitCode;
    }

    /// <summary>The job the command names, or null after saying why there is none (exit 2, as for <c>queue retry</c>).</summary>
    private async Task<JobRecord?> FindJobAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(command.JobId, out var id))
        {
            WriteError($"'{command.JobId}' is not a job ID. '{command.Hint("queue list")}' shows them.");
            return null;
        }
        var job = await repository.GetAsync(configuration.Settings.Database.Path, id, cancellationToken);
        if (job is null) WriteError($"No job has ID {id:N}.");
        return job;
    }

    private static int Refuse(string message)
    {
        WriteError(message);
        return (int)ExitCode.GeneralFailure;
    }

    /// <param name="verb">How the verb names itself in "To hold it, press Ctrl+C twice".</param>
    /// <param name="notWaitingAction">How the verb ends the sentence "Only waiting and stopped videos can …", such as <c>be held</c>; null for a verb that never says it.</param>
    private static string RefusalText(VerbRefused refused, CliCommand command, string verb, string? notWaitingAction)
    {
        var job = refused.Job;
        var name = Path.GetFileName(job.SourcePath);
        return refused.Reason switch
        {
            VerbRefusal.ConvertingNow => $"{name} is converting now ({StatusWord(job.Status)}). To {verb} it, press Ctrl+C twice in the run's window first.",
            VerbRefusal.StoppedWhileFinalising => $"{name} was stopped while being finalised; '{command.Hint($"finalize {job.Id:N}")}' checks and resumes it.",
            VerbRefusal.BeingFinalised => $"{name} is being finalised now.",
            VerbRefusal.NotReady => $"Only a video that is ready to finalise can be rejected; {name} {StatusPhrase(job)}.",
            _ => $"Only waiting and stopped videos can {notWaitingAction ?? throw new InvalidOperationException($"{verb} never refuses a job for not waiting.")}; {name} {StatusPhrase(job)}."
        };
    }

    private static string OnlyCopyText(OnlyCopyAtRisk onlyCopy, string verb)
    {
        var job = onlyCopy.Job;
        var name = Path.GetFileName(job.SourcePath);
        return onlyCopy.Reason switch
        {
            OnlyCopyReason.OriginalMissing => $"The original of {name} is missing, so its AV1 at {job.OutputPath} may be the only copy. Put the original back, or move the AV1 somewhere safe, then {verb} the video.",
            OnlyCopyReason.ImmichNotConfigured => ImmichUnusable("immich.baseUrl isn't set", name),
            OnlyCopyReason.ImmichUnavailable => ImmichUnusable(onlyCopy.Detail!.TrimEnd('.'), name),
            _ => $"The original of {name} is gone from Immich, so its AV1 and downloaded copy may be the only copies: {job.OutputPath}, {job.SourcePath}. Move them somewhere safe, then {verb} the video."
        };
    }

    private static string ImmichUnusable(string why, string name) =>
        $"Immich can't be used ({why}), so video-optimiser can't check that the original of {name} is still there. Try again once it answers.";

    /// <summary>
    /// W3: a remote CRF search or encode runs detached, so a stop leaves it going. The next run that reaches the worker, managed
    /// or manual, stops it and removes its workspace (VOPT-90), so one line serves both.
    /// </summary>
    private static string RemoteStageLine(RemoteStageNote note) =>
        $"Its {(note.Stage == RemoteStage.Encode ? "encode" : "CRF search")} may still be running on the worker; the next run on that worker stops it.";

    private static string LeftFilesLine(LeftFiles files, CliCommand command)
    {
        var cleanup = command.Hint("cleanup");
        var av1 = files.Av1 switch
        {
            LeftAv1.Finished => "AV1",
            LeftAv1.Unfinished => "unfinished AV1",
            _ => null
        };
        return (av1, files.DownloadedCopy) switch
        {
            (null, _) => $"Its downloaded copy stays until '{cleanup}' deletes it.",
            (_, false) => $"Its {av1} stays until '{cleanup}' deletes it.",
            _ => $"Its {av1} and downloaded copy stay until '{cleanup}' deletes them."
        };
    }

    public async Task<int> AddAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var settings = configuration.Settings;
        if (discovery.Refusal(settings) is QueueDiscoveryRefusal.NoWatchRoots)
        {
            WriteError($"queue add needs at least one watch.roots entry. For Immich videos, use '{command.Hint("queue add immich")}'.");
            return (int)ExitCode.InvalidConfiguration;
        }

        CliConsole.WriteLine($"Scanning {OutputWords.Count(settings.Watch.Roots.Count, "watch root", "watch roots")} for videos to convert; files not seen before are probed with ffprobe.");
        var result = await discovery.DiscoverAsync(settings.Database.Path, settings, command.First, new QueueDiscoverConsole(CliConsole.Out), cancellationToken);
        QueueDiscoverConsole.WriteSummary(CliConsole.Out, result, command);
        if (result.QueuedPaths.Count + result.AlreadyQueued > 0) CliConsole.WriteLine(NextRunLine(discovery.NextRun(settings.Database.Path), command));
        if (result.ReadyToFinalize > 0) CliConsole.WriteLine(NextSteps.ReadyToFinaliseLine(result.ReadyToFinalize, command));
        return result.Issues == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }

    public async Task<int> AddFileAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var settings = configuration.Settings;
        var result = await discovery.AddFileAsync(settings.Database.Path, settings, command.FilePath!, command.Force, cancellationToken);
        var (path, job) = (result.Path, result.Job);
        var name = Path.GetFileName(path);
        var anyway = $"To queue it anyway: {command.Hint($"queue add \"{path}\" --force")}";
        switch (result.Outcome)
        {
            case QueueAddOutcome.Queued:
                CliConsole.WriteLine($"Queued {name} as job {job!.Id:N}{(result.OutsideRules ? ", outside your rules (--force)" : string.Empty)}.");
                var ahead = await discovery.AheadOfAsync(settings.Database.Path, job, cancellationToken);
                CliConsole.WriteLine(ahead == 0 ? NextRunLine(discovery.NextRun(settings.Database.Path), command)
                    : $"{(ahead == 1 ? "1 video is" : $"{ahead} videos are")} ahead of it; '{command.Hint($"queue next {job.Id:N}")}' puts it first.");
                return (int)ExitCode.Success;
            case QueueAddOutcome.Missing:
                WriteError($"Original does not exist: {path}");
                return (int)ExitCode.ProcessingFailure;
            case QueueAddOutcome.MadeByTool:
                WriteError($"{path} is a file video-optimiser made, so it can't be queued.");
                return (int)ExitCode.NoEligibleFiles;
            case QueueAddOutcome.Archived:
                WriteError($"{path} is an original video-optimiser archived, so it can't be queued.");
                return (int)ExitCode.NoEligibleFiles;
            case QueueAddOutcome.OpenJob:
                CliConsole.WriteLine(result.OpenState == OpenJobState.LastFinaliseFailed
                    ? $"{name}'s last finalise failed (job {job!.Id:N}): {job.FailureMessage}"
                    : $"{name} already has job {job!.Id:N} ({OpenStatusPhrase(job, result.OpenState!.Value)}).");
                return (int)ExitCode.Success;
            case QueueAddOutcome.Settled:
                CliConsole.WriteLine(job!.Status switch
                {
                    JobStatus.Completed => $"{name} was already converted (job {job.Id:N}).",
                    JobStatus.Cancelled => $"{name} was dropped before (job {job.Id:N}). To try it again: {command.Hint($"queue retry {job.Id:N}")}",
                    JobStatus.Skipped => $"{name} was not worth converting before (job {job.Id:N}). To try it again: {command.Hint($"queue retry {job.Id:N}")}",
                    JobStatus.Rejected => $"{name} was rejected before (job {job.Id:N}). To try it again: {command.Hint($"queue retry {job.Id:N}")}",
                    _ => $"{name} failed before (job {job.Id:N}). To try it again: {command.Hint($"queue retry {job.Id:N}")}"
                });
                return (int)ExitCode.Success;
            case QueueAddOutcome.OutsideRoots:
                WriteError($"{path} is outside your watch roots. {anyway}");
                return (int)ExitCode.NoEligibleFiles;
            case QueueAddOutcome.Ineligible:
                WriteError($"{name} doesn't fit your rules: {Fragment(result.Reason!)}. {anyway}");
                return (int)ExitCode.NoEligibleFiles;
            case QueueAddOutcome.TooSmall:
                WriteError($"{name} doesn't fit your rules: {Fragment(result.Reason!)}.");
                return (int)ExitCode.NoEligibleFiles;
            default:
                // Unreadable or unprobeable; every probe failure names ffprobe itself.
                WriteError($"Can't queue {name}: {Fragment(result.Reason!)}.");
                return (int)ExitCode.ProcessingFailure;
        }
    }

    public async Task<int> RetryAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(command.JobId, out var id))
        {
            WriteError("Job ID is invalid.");
            return (int)ExitCode.InvalidArguments;
        }
        var result = await retryService.RetryAsync(configuration.Settings.Database.Path, id, configuration.Settings, cancellationToken);
        if (!result.Succeeded)
        {
            WriteError(result.Error);
            return (int)ExitCode.InvalidArguments;
        }

        var replacement = result.Replacement!;
        CliConsole.WriteLine($"Queued {Path.GetFileName(replacement.SourcePath)} again as job {replacement.Id:N}.");
        if (result.DownloadedCopyGone) CliConsole.WriteLine($"Its downloaded copy is gone; '{command.Hint("run")}' downloads it again first.");
        CliConsole.WriteLine(NextRunLine(discovery.NextRun(configuration.Settings.Database.Path), command));
        return (int)ExitCode.Success;
    }

    /// <param name="terminal">The ended jobs (<c>history</c>) rather than the open ones (<c>queue list</c>).</param>
    public async Task<int> ListAsync(LoadedConfiguration configuration, CliCommand command, bool terminal, CancellationToken cancellationToken)
    {
        var databasePath = configuration.Settings.Database.Path;
        if (!terminal && !command.Json)
        {
            var open = await discovery.OpenJobsAsync(databasePath, cancellationToken);
            foreach (var line in open.Count == 0 ? ["Nothing is queued or ready to finalise."] : OpenJobLines(open, command)) CliConsole.WriteLine(line);
            return (int)ExitCode.Success;
        }

        var jobs = await repository.ListAsync(databasePath, terminal, cancellationToken);
        if (command.Json)
        {
            var output = jobs.Select(job => new JobJsonOutput(job.Id.ToString("N"), job.Status.ToString(), job.Held, job.SourcePath, job.SourceKind == JobSourceKind.Immich ? "immich" : "file", job.ImmichAssetId, job.ImmichNewAssetId, job.Crf, job.OutputPath, job.PercentageSaved, job.FailureCategory, job.FailureMessage, job.ValidationWarnings, job.UpdatedUtc)).ToArray();
            CliConsole.WriteLine(JsonSerializer.Serialize(output, QueueJsonContext.Default.JobJsonOutputArray));
        }
        else if (jobs.Count == 0)
        {
            CliConsole.WriteLine("No job history.");
        }
        else
        {
            foreach (var job in jobs)
            {
                var suffix = job.FailureMessage is null || FailureWords.Describe(job.FailureCategory, job.FailureMessage, sentence: false) is not { } detail ? string.Empty : $", {detail}";
                var savings = job.PercentageSaved is null ? string.Empty : $" {job.PercentageSaved:F1}% saved";
                // An Immich job's file name is the original's in Immich; the asset IDs find it there.
                var immich = job.ImmichAssetId is { } asset ? $" Immich {asset}{(job.ImmichNewAssetId is { } av1 ? $" -> {av1}" : string.Empty)}" : string.Empty;
                CliConsole.WriteLine($"{job.Id:N}  {StatusName(job.Status),-20} {Path.GetFileName(job.SourcePath)}{immich}{(job.Crf is null ? string.Empty : $" CRF {job.Crf}")}{savings}{suffix}");
            }
        }

        return (int)ExitCode.Success;
    }
}
