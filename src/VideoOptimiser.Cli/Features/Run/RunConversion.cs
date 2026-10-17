using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// What <c>run</c> and <c>immich run</c> share at the console: the banner, then <see cref="RunFlow"/> (the whole queue
/// converted, folder and Immich videos alike, the worker, each video in turn) with the console as its front end
/// (<see cref="ConsoleRunObserver"/> prints, <see cref="ConsoleRunQuestions"/> asks, and the event file records the same events).
/// <c>immich run</c> adds its videos to the queue first.
/// </summary>
internal sealed class RunConversion(IRemoteWorkerLifecycle lifecycle, RunFlow flow, RunWorkerState state, RunStop stop, EventLogRunObserver eventLog)
{
    /// <summary>
    /// Prints <see cref="RunBanner"/> before any download or encoding, so a config still set to <c>remoteSsh</c> is noticed
    /// before it costs anything. A retained managed worker comes from the local state file, with no Hetzner API call.
    /// </summary>
    public async Task PrintBannerAsync(LoadedConfiguration configuration, ProcessingModeOverride? modeOverride, CancellationToken cancellationToken)
    {
        var worker = lifecycle.IsManaged(configuration.Settings)
            ? await lifecycle.ReadRecordedAsync(configuration.Settings.Database.Path, cancellationToken)
            : null;
        CliConsole.WriteLine(RunBanner.Describe(configuration.Settings, worker, modeOverride?.Flag));
    }

    /// <summary>
    /// Everything <c>run</c> does after its banner (<see cref="RunFlow.ConvertQueueAsync"/>), printed. With nothing to convert,
    /// the run command's guard decides the worker's fate (<see cref="RunWorkerEnd.GuardAsync"/>).
    /// </summary>
    /// <param name="beforeNext">Printed between the failed jobs and the line that says what to finalise.</param>
    public Task<ExitCode> ConvertQueueAsync(LoadedConfiguration configuration, CliCommand command, Action? beforeNext, CancellationToken cancellationToken) =>
        flow.ConvertQueueAsync(configuration.Settings, command.ModeOverride is not null, new TeeRunObserver(new ConsoleRunObserver(configuration, command, stop, state, beforeNext), eventLog), new ConsoleRunQuestions(state), stop.AfterThisVideo, cancellationToken);
}
