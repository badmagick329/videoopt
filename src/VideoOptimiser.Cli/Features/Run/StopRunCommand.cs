using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// <c>run stop [--now | --undo]</c>: stops the background run going on this database the way Ctrl+C stops a run in a console,
/// after the video that converts or at once, or withdraws a stop after this video before that video finishes (VOPT-188). A
/// stop now is final. A run in a console is stopped in that console, so this refuses it. Changed wording goes through the
/// humanizer gate (docs index).
/// </summary>
internal sealed class StopRunCommand(StopRunRequest stopRequest)
{
    public async Task<int> RunAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (command.UndoStop) return await WithdrawAsync(configuration, command, cancellationToken);

        var result = await stopRequest.StopAsync(configuration.Settings.Database.Path, command.StopNow, cancellationToken);
        switch (result.Outcome)
        {
            case StopRunOutcome.NoRun:
                CliConsole.WriteLine("No run is going.");
                return (int)ExitCode.Success;
            case StopRunOutcome.Foreground:
                return RefuseForeground(result.Holder!);
            default:
                CliConsole.WriteLine(command.StopNow ? "Asked the run to stop now." : "Asked the run to stop after this video.");
                CliConsole.WriteLine($"'{command.Hint("status")}' shows when it has stopped.");
                return (int)ExitCode.Success;
        }
    }

    /// <remarks>
    /// Withdrawn promises only that the run carries on after this video: a run that already read the stop between videos ends
    /// anyway, and the user chose to accept that late withdraw without reporting it (VOPT-186).
    /// </remarks>
    private async Task<int> WithdrawAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var result = await stopRequest.WithdrawAsync(configuration.Settings.Database.Path, cancellationToken);
        switch (result.Outcome)
        {
            case WithdrawStopOutcome.NoRun:
                CliConsole.WriteLine("No run is going.");
                return (int)ExitCode.Success;
            case WithdrawStopOutcome.Foreground:
                return RefuseForeground(result.Holder!);
            case WithdrawStopOutcome.NoStopAsked:
                CliConsole.WriteLine("No stop is asked of the run.");
                return (int)ExitCode.Success;
            case WithdrawStopOutcome.StoppingNow:
                WriteError("The run is already stopping now, and that can't be undone.");
                return (int)ExitCode.GeneralFailure;
            default:
                CliConsole.WriteLine("Asked the run to carry on after this video.");
                CliConsole.WriteLine($"'{command.Hint("status")}' shows what it is doing.");
                return (int)ExitCode.Success;
        }
    }

    private static int RefuseForeground(LiveRunHolder holder)
    {
        WriteError($"The run going now is in a terminal (process {holder.ProcessId}): stop it with Ctrl+C in its window.");
        return (int)ExitCode.GeneralFailure;
    }
}
