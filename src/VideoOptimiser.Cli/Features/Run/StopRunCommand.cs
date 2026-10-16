using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Domain;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// <c>run stop [--now]</c>: stops the background run going on this database the way Ctrl+C stops a run in a console, after the
/// video that converts or at once. A run in a console is stopped in that console, so this refuses it. Changed wording goes
/// through the humanizer gate (docs index).
/// </summary>
internal sealed class StopRunCommand(StopRunRequest stopRequest)
{
    public async Task<int> RunAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var result = await stopRequest.StopAsync(configuration.Settings.Database.Path, command.StopNow, cancellationToken);
        switch (result.Outcome)
        {
            case StopRunOutcome.NoRun:
                Console.WriteLine("No run is going.");
                return (int)ExitCode.Success;
            case StopRunOutcome.Foreground:
                WriteError($"The run going now is in a terminal (process {result.Holder!.ProcessId}): stop it with Ctrl+C in its window.");
                return (int)ExitCode.GeneralFailure;
            default:
                Console.WriteLine(command.StopNow ? "Asked the run to stop now." : "Asked the run to stop after this video.");
                Console.WriteLine($"'{command.Hint("status")}' shows when it has stopped.");
                return (int)ExitCode.Success;
        }
    }
}
