using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Domain;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// The hidden verbs behind background runs, absent from <c>--help</c> because users start a background run from the TUI:
/// <c>background start</c> launches the runner detached and waits for it to hold the run lock, and <c>background runner</c> is
/// the detached process itself. The TUI calls the same Application code. The runner has no console, so nothing it prints is
/// seen: its output is the event file.
/// </summary>
internal sealed class BackgroundCommands(ISettingsValidator settingsValidator, BackgroundLaunch launch, BackgroundRun background)
{
    public async Task<int> StartAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (ReportInvalidSettings(settingsValidator, configuration)) return (int)ExitCode.InvalidConfiguration;

        var result = await launch.StartAsync(configuration.Settings.Database.Path, RunnerArguments(configuration, command), Environment.CurrentDirectory, cancellationToken);
        switch (result.Outcome)
        {
            case BackgroundLaunchOutcome.Started:
                Console.WriteLine($"Started a background run (process {result.ProcessId}).");
                Console.WriteLine($"'{command.Hint("status")}' shows how it is going; '{command.Hint("run stop")}' stops it.");
                return (int)ExitCode.Success;
            case BackgroundLaunchOutcome.AlreadyRunning:
                RunWorkerEnd.WriteRefusal(result.Holder, command);
                return (int)ExitCode.GeneralFailure;
            case BackgroundLaunchOutcome.ExitedEarly:
                WriteError($"The background run quit before it could start (exit code {result.ExitCode}).");
                return (int)ExitCode.GeneralFailure;
            default:
                WriteError($"The background run (process {result.ProcessId}) has not started yet. '{command.Hint("status")}' shows whether it has.");
                return (int)ExitCode.GeneralFailure;
        }
    }

    /// <summary>
    /// The runner gets the full path of the configuration, since its working directory is the launcher's but it must not depend
    /// on that, and a mode flag only when the launcher was given one.
    /// </summary>
    private static List<string> RunnerArguments(LoadedConfiguration configuration, CliCommand command)
    {
        List<string> arguments = ["background", "runner", "--worker-fate", command.KeepWorker ? "keep" : "delete", "--config", Path.GetFullPath(configuration.Path)];
        if (command.ModeOverride is not null) arguments.Add(command.ModeOverride.Flag);
        return arguments;
    }

    public Task<int> RunnerAsync(LoadedConfiguration configuration, CliCommand command) =>
        background.RunAsync(configuration.Settings, command.ModeOverride is not null, command.WorkerFate!.Value);
}
