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
/// The queue commands: the verbs <c>queue hold</c>, <c>release</c>, <c>next</c> and <c>drop</c> (VOPT-54 step 5); the
/// commands that put videos in the queue, <c>queue add</c> (was <c>queue discover</c>), <c>queue add &lt;file&gt;</c> and
/// <c>queue retry</c>, each ending with what to run next; and the job lists <c>queue list</c>, <c>status</c> and
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
                Console.WriteLine($"{name} is already held.");
                return (int)ExitCode.Success;
            default:
                Console.WriteLine($"Held {name}. Runs pass over it until '{command.Hint($"queue release {outcome.Job.Id:N}")}'.");
                return (int)ExitCode.Success;
        }
    }

    public async Task<int> ReleaseAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (await FindJobAsync(configuration, command, cancellationToken) is not { } job) return (int)ExitCode.InvalidArguments;
        var outcome = await verbs.ReleaseAsync(configuration.Settings.Database.Path, job, cancellationToken);
        var name = Path.GetFileName(outcome.Job.SourcePath);
        Console.WriteLine(outcome is JobNotHeld ? $"{name} isn't held." : $"Released {name}; runs take it again.");
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
                Console.WriteLine($"{name} is converting now.");
                return (int)ExitCode.Success;
            case VerbRefused refused:
                return Refuse(RefusalText(refused, command, verb: "run next", notWaitingAction: "run next"));
            case JobMovedNext moved:
                Console.WriteLine($"{name} {(moved.RunTakesIt ? "runs next" : "is now first in the queue")}{(moved.WasHeld ? "; it's no longer held" : string.Empty)}.");
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
                return Refuse(OnlyCopyText(onlyCopy));
            case JobDropped dropped:
                Console.WriteLine($"Dropped {name}.");
                if (dropped.RemoteStage is { } remote) Console.WriteLine(RemoteStageLine(remote));
                if (await verbs.LeftFilesAsync(configuration.Settings, dropped.Job, dropped.HadFinishedAv1, cancellationToken) is { } files) Console.WriteLine(LeftFilesLine(files, command));
                if (dropped.UploadedAssetId is { } uploaded) Console.WriteLine($"The AV1 already uploaded to Immich as asset {uploaded} stays there; delete it in Immich if you do not want it.");
                return (int)ExitCode.Success;
            default:
                throw new InvalidOperationException($"queue drop cannot end as {outcome.GetType().Name}.");
        }
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
                    Console.WriteLine("Nothing to drop: the queue is empty.");
                    return (int)ExitCode.Success;
                case ChangedUnderDropAll changed:
                    WriteError(new JobChangedException(changed.JobId).Message);
                    exitCode = ExitCode.GeneralFailure;
                    break;
                case DroppedInBulk dropped:
                    Console.WriteLine($"Dropped {Path.GetFileName(dropped.Job.SourcePath)}.");
                    if (dropped.RemoteStage is { } remote) Console.WriteLine(RemoteStageLine(remote));
                    break;
                case LeftStoppedFinalising left:
                    Console.WriteLine($"Left 1 video stopped while being finalised; '{command.Hint($"finalize {left.Job.Id:N}")}' checks and resumes it.");
                    break;
                case LeftStoppedChecking left:
                    Console.WriteLine($"Left 1 video stopped while its AV1 was checked; to drop it: {command.Hint($"queue drop {left.Job.Id:N}")}");
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
            _ => $"Only waiting and stopped videos can {notWaitingAction ?? throw new InvalidOperationException($"{verb} never refuses a job for not waiting.")}; {name} {StatusPhrase(job)}."
        };
    }

    private static string OnlyCopyText(OnlyCopyAtRisk onlyCopy)
    {
        var job = onlyCopy.Job;
        var name = Path.GetFileName(job.SourcePath);
        return onlyCopy.Reason switch
        {
            OnlyCopyReason.OriginalMissing => $"The original of {name} is missing, so its AV1 at {job.OutputPath} may be the only copy. Put the original back, or move the AV1 somewhere safe, then drop the video.",
            OnlyCopyReason.ImmichNotConfigured => ImmichUnusable("immich.baseUrl isn't set", name),
            OnlyCopyReason.ImmichUnavailable => ImmichUnusable(onlyCopy.Detail!.TrimEnd('.'), name),
            _ => $"The original of {name} is gone from Immich, so its AV1 and downloaded copy may be the only copies: {job.OutputPath}, {job.SourcePath}. Move them somewhere safe, then drop the video."
        };
    }

    private static string ImmichUnusable(string why, string name) =>
        $"Immich can't be used ({why}), so video-optimiser can't check that the original of {name} is still there. Try again once it answers.";

    /// <summary>W3: a remote CRF search or encode runs detached, so a stop leaves it going. A managed worker's deletion ends it; a manual worker's runs until it finishes.</summary>
    private static string RemoteStageLine(RemoteStageNote note)
    {
        var stage = note.Stage == RemoteStage.Encode ? "encode" : "CRF search";
        return note.ManagedWorker
            ? $"Its {stage} may still be running on the worker; deleting the worker stops it."
            : $"Its {stage} may still be running on the worker until it finishes.";
    }

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
            WriteError("queue add needs at least one watch.roots entry. For Immich videos, use 'immich run'.");
            return (int)ExitCode.InvalidConfiguration;
        }

        Console.WriteLine($"Scanning {OutputWords.Count(settings.Watch.Roots.Count, "watch root", "watch roots")} for videos to convert; files not seen before are probed with ffprobe.");
        var result = await discovery.DiscoverAsync(settings.Database.Path, settings, command.First, new QueueDiscoverConsole(Console.Out), cancellationToken);
        QueueDiscoverConsole.WriteSummary(Console.Out, result);
        if (result.QueuedPaths.Count + result.AlreadyQueued > 0) Console.WriteLine(NextRunLine(discovery.NextRun(settings.Database.Path), command));
        if (result.ReadyToFinalize > 0) Console.WriteLine(ReadyToFinaliseLine(result.ReadyToFinalize, command));
        return result.Issues == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }

    public async Task<int> AddFileAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var settings = configuration.Settings;
        var result = await discovery.AddFileAsync(settings.Database.Path, settings, command.FilePath!, command.Force, cancellationToken);
        var (path, job) = (result.Path, result.Job);
        var name = Path.GetFileName(path);
        var anyway = $"To queue it anyway: video-optimiser queue add \"{path}\" --force{ConfigArgument(command)}";
        switch (result.Outcome)
        {
            case QueueAddOutcome.Queued:
                Console.WriteLine($"Queued {name} as job {job!.Id:N}{(result.OutsideRules ? ", outside your rules (--force)" : string.Empty)}.");
                var ahead = await discovery.AheadOfAsync(settings.Database.Path, job, cancellationToken);
                Console.WriteLine(ahead == 0 ? NextRunLine(discovery.NextRun(settings.Database.Path), command)
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
                Console.WriteLine(result.OpenState == OpenJobState.LastFinaliseFailed
                    ? $"{name}'s last finalise failed (job {job!.Id:N}): {job.FailureMessage}"
                    : $"{name} already has job {job!.Id:N} ({OpenStatusPhrase(job, result.OpenState!.Value)}).");
                return (int)ExitCode.Success;
            case QueueAddOutcome.Settled:
                Console.WriteLine(job!.Status switch
                {
                    JobStatus.Completed => $"{name} was already converted (job {job.Id:N}).",
                    JobStatus.Cancelled => $"{name} was dropped before (job {job.Id:N}). To try it again: {command.Hint($"queue retry {job.Id:N}")}",
                    JobStatus.Skipped => $"{name} was not worth converting before (job {job.Id:N}). To try it again: {command.Hint($"queue retry {job.Id:N}")}",
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
        Console.WriteLine($"Queued {Path.GetFileName(replacement.SourcePath)} again as job {replacement.Id:N}.");
        if (result.DownloadedCopyGone) Console.WriteLine($"Its downloaded copy is gone; '{command.Hint("run")}' downloads it again first.");
        Console.WriteLine(NextRunLine(discovery.NextRun(configuration.Settings.Database.Path), command));
        return (int)ExitCode.Success;
    }

    public async Task<int> ListAsync(LoadedConfiguration configuration, bool terminal, bool json, CancellationToken cancellationToken)
    {
        var jobs = await repository.ListAsync(configuration.Settings.Database.Path, terminal, cancellationToken);
        if (json)
        {
            var output = jobs.Select(job => new JobJsonOutput(job.Id.ToString("N"), job.Status.ToString(), job.SourcePath, job.SourceKind == JobSourceKind.Immich ? "immich" : "file", job.ImmichAssetId, job.ImmichNewAssetId, job.Crf, job.OutputPath, job.PercentageSaved, job.FailureCategory, job.FailureMessage, job.UpdatedUtc)).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(output, QueueJsonContext.Default.JobJsonOutputArray));
        }
        else if (jobs.Count == 0)
        {
            Console.WriteLine(terminal ? "No job history." : "No active jobs.");
        }
        else
        {
            foreach (var job in jobs)
            {
                var suffix = job.FailureMessage is null ? string.Empty : $" — {job.FailureCategory}: {job.FailureMessage}";
                var savings = job.PercentageSaved is null ? string.Empty : $" {job.PercentageSaved:F1}% saved";
                var resume = TemporaryOutputClassifier.DescribeResume(job) is { } behaviour ? $" ({behaviour})" : string.Empty;
                // An Immich job's file name is the original's in Immich; the asset IDs find it there.
                var immich = job.ImmichAssetId is { } asset ? $" Immich {asset}{(job.ImmichNewAssetId is { } av1 ? $" -> {av1}" : string.Empty)}" : string.Empty;
                Console.WriteLine($"{job.Id:N}  {job.Status,-16} {Path.GetFileName(job.SourcePath)}{immich}{(job.Crf is null ? string.Empty : $" CRF {job.Crf}")}{savings}{suffix}{resume}");
            }
        }

        return (int)ExitCode.Success;
    }
}
