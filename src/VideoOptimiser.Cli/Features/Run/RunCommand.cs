using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// <c>run</c> (VOPT-54 step 5): converts every queued video, folder and Immich alike, taking each next one from the queue as
/// it is then. Changed wording goes through the humanizer gate (docs index).
/// </summary>
internal sealed class RunCommand(ISettingsValidator settingsValidator, RunConversion conversion, RunWorkerEnd workerEnd)
{
    public Task<int> RunAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken) =>
        workerEnd.GuardAsync(configuration, command, () => ExecuteAsync(configuration, command, cancellationToken));

    private async Task<int> ExecuteAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (ReportInvalidSettings(settingsValidator, configuration)) return (int)ExitCode.InvalidConfiguration;
        await using var runLock = await workerEnd.TakeRunLockAsync(configuration, command, LockHolders.Run, cancellationToken);
        if (runLock is null) return (int)ExitCode.GeneralFailure;
        await conversion.PrintBannerAsync(configuration, command.ModeOverride, cancellationToken);
        return (int)await conversion.ConvertQueueAsync(configuration, command, beforeNext: null, cancellationToken);
    }
}
