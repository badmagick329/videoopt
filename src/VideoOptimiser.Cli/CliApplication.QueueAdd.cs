using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Jobs;

/// <summary>
/// <c>queue add</c> (was <c>queue discover</c>), <c>queue add &lt;file&gt;</c> and <c>queue retry</c>: the commands that put
/// videos in the queue, each ending with what to run next. The wording was approved by the user word for word; ask before
/// changing it.
/// </summary>
internal static partial class CliApplication
{
    private static async Task<int> RunQueueAddAsync(IQueueService queue, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        // Settings validation allows no roots when Immich is configured; scanning nothing would only print "Queued: 0".
        if (configuration.Settings.Watch.Roots.Count == 0)
        {
            WriteError("queue add needs at least one watch.roots entry. For Immich videos, use 'immich run'.");
            return (int)ExitCode.InvalidConfiguration;
        }

        Console.WriteLine($"Scanning {OutputWords.Count(configuration.Settings.Watch.Roots.Count, "watch root", "watch roots")} for videos to convert; files not seen before are probed with ffprobe.");
        var result = await queue.DiscoverAsync(configuration.Settings.Database.Path, configuration.Settings, command.First, new QueueDiscoverConsole(Console.Out), cancellationToken);
        QueueDiscoverConsole.WriteSummary(Console.Out, result);
        if (result.QueuedPaths.Count + result.AlreadyQueued > 0) Console.WriteLine(NextRunLine(configuration, command));
        if (result.ReadyToFinalize > 0) Console.WriteLine(ReadyToFinaliseLine(result.ReadyToFinalize, command));
        return result.Issues == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }

    private static async Task<int> RunQueueAddFileAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var settings = configuration.Settings;
        var result = await services.GetRequiredService<IQueueService>().AddFileAsync(settings.Database.Path, settings, command.FilePath!, command.Force, cancellationToken);
        var (path, job) = (result.Path, result.Job);
        var name = Path.GetFileName(path);
        var anyway = $"To queue it anyway: video-optimiser queue add \"{path}\" --force{ConfigArgument(command)}";
        switch (result.Outcome)
        {
            case QueueAddOutcome.Queued:
                Console.WriteLine($"Queued {name} as job {job!.Id:N}{(result.OutsideRules ? ", outside your rules (--force)" : string.Empty)}.");
                var ahead = (await services.GetRequiredService<IJobRepository>().ListAsync(settings.Database.Path, terminal: false, cancellationToken))
                    .Count(other => other.Id != job.Id && other.IsQueued && !other.Held && other.ResumeStatus != JobStatus.Finalizing);
                Console.WriteLine(ahead == 0 ? NextRunLine(configuration, command)
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
                Console.WriteLine(job is { Status: JobStatus.ReadyToFinalize, FailureMessage: { } finaliseFailure }
                    ? $"{name}'s last finalise failed (job {job.Id:N}): {finaliseFailure}"
                    : $"{name} already has job {job!.Id:N} ({OpenStatusPhrase(job, settings.Database.Path)}).");
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
            case QueueAddOutcome.Unreadable:
                WriteError($"{name} can't be read, so it can't be queued: {Fragment(result.Reason!)}.");
                return (int)ExitCode.ProcessingFailure;
            default:
                WriteError($"ffprobe can't read {name}, so it can't be queued: {Fragment(result.Reason!)}.");
                return (int)ExitCode.ProcessingFailure;
        }
    }

    private static async Task<int> RunQueueRetryAsync(IJobRetryService retryService, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
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
        if (replacement.SourceKind == JobSourceKind.Immich && !File.Exists(replacement.SourcePath)) Console.WriteLine($"Its downloaded copy is gone; '{command.Hint("run")}' downloads it again first.");
        Console.WriteLine(NextRunLine(configuration, command));
        return (int)ExitCode.Success;
    }

    /// <summary>
    /// What to run once videos are queued. A run going now takes them at its next video; an <c>immich run</c> keeps the
    /// videos it started with (until phase A step 7), as does a run from an older version, so they wait for it to end.
    /// </summary>
    private static string NextRunLine(LoadedConfiguration configuration, CliCommand command) => LiveRun(configuration.Settings.Database.Path) switch
    {
        null => $"Next: run '{command.Hint("run")}'.",
        RunHolder => "The run going now picks them up.",
        _ => $"Next: run '{command.Hint("run")}' once the run going now ends."
    };

    private static string ReadyToFinaliseLine(int count, CliCommand command) => count == 1
        ? $"1 video is ready to finalise: review it, then run '{command.Hint("finalize --ready")}'."
        : $"{count} videos are ready to finalise: review them, then run '{command.Hint("finalize --ready")}'.";

    /// <summary>The open status in the glossary's words; a converting status with no run going is a stop a crash left.</summary>
    private static string OpenStatusPhrase(JobRecord job, string databasePath) => job switch
    {
        { Held: true } => "held",
        { Status: JobStatus.ReadyToFinalize } => "ready to finalise",
        { Status: JobStatus.Interrupted, ResumeStatus: JobStatus.Finalizing } => "stopped while being finalised",
        { Status: JobStatus.Finalizing } => FinaliseGoing(databasePath) ? "finalising" : "stopped while being finalised",
        _ when IsConverting(job.Status) && LiveRun(databasePath) is null => "stopped",
        _ => StatusWord(job.Status)
    };

    /// <summary>A scanner reason ("Extension is not allowed.") as the end of a sentence.</summary>
    private static string Fragment(string reason) => reason.Length == 0 ? reason : char.ToLowerInvariant(reason[0]) + reason[1..].TrimEnd('.');

    private static string ConfigArgument(CliCommand command) => command.ConfigurationPath is null ? string.Empty : $" --config \"{command.ConfigurationPath}\"";
}
