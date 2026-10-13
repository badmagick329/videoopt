using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Cli;

/// <summary>
/// What Ctrl+C on a remote run prints. Ctrl+C stops only this side: a CRF search or encode keeps running on the worker,
/// which bills hourly until it is deleted, so the lines say what is still running, what it costs, and the command that
/// picks it up again. The wording was approved by the user (VOPT-47); ask before changing it.
/// </summary>
internal static class StoppedRunMessage
{
    /// <param name="interrupted">The job Ctrl+C stopped, or null when it came between jobs or before the first one.</param>
    /// <param name="worker">The managed server's name, or the SSH host of a manual one.</param>
    /// <param name="managed">A manual host is not billed or deleted by this tool, so it gets only the lines that hold for it.</param>
    public static IReadOnlyList<string> Lines(JobInterruptedException? interrupted, string worker, bool managed, CliCommand command)
    {
        var rerun = $"video-optimiser {RerunCommand(command)}";
        var delete = $"video-optimiser worker delete{ConfigArgument(command)}";
        var stillRunning = interrupted is { Remote: true, Stage: JobStatus.CrfSearching or JobStatus.Encoding }
            ? $"{(interrupted.Stage == JobStatus.Encoding ? "The encode" : "The CRF search")} of {Path.GetFileName(interrupted.Job.SourcePath)} is still running on the worker {worker}."
            : null;
        if (!managed) return stillRunning is null ? [] : [$"Stopped watching. {stillRunning}", $"To pick it up again: {rerun}"];
        if (stillRunning is not null)
        {
            return
            [
                $"Stopped watching. {stillRunning}",
                "The worker is billed hourly until it is deleted.",
                $"To pick it up again: {rerun}",
                $"To stop it instead: {delete} (the work in progress is lost)"
            ];
        }
        return
        [
            interrupted is { Remote: true, Stage: JobStatus.Downloading }
                ? $"Stopped. The encoded output is waiting on the worker {worker}, which stays up, billed hourly."
                : $"Stopped. Nothing is running on the worker {worker}, but it stays up, billed hourly, so the job can resume.",
            $"To resume: {rerun}",
            $"To delete it instead: {delete}"
        ];
    }

    /// <summary>The command the user ran, with the flags that change what it does; <c>--config</c> only when it was typed.</summary>
    public static string RerunCommand(CliCommand command)
    {
        var verb = command.Kind switch
        {
            CliCommandKind.Process => $"process \"{command.ProcessPath}\"{(command.Force ? " --force" : string.Empty)}",
            CliCommandKind.QueueRun => "queue run",
            // --first is parsed as --limit 1, so the flag the user typed is checked first.
            CliCommandKind.ImmichRun => $"immich run{(command.First ? " --first" : command.Limit is { } limit ? $" --limit {limit}" : string.Empty)}",
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Only processing commands can be stopped mid-run.")
        };
        return $"{verb}{(command.ModeOverride is null ? string.Empty : $" {command.ModeOverride.Flag}")}{ConfigArgument(command)}";
    }

    private static string ConfigArgument(CliCommand command) =>
        command.ConfigurationPath is null ? string.Empty : $" --config \"{command.ConfigurationPath}\"";
}
