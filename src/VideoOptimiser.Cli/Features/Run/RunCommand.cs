using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// <c>run</c> (VOPT-54 step 5): converts every queued video, folder and Immich alike, taking each next one from the queue as
/// it is then. Changed wording goes through the humanizer gate (docs index).
/// </summary>
internal sealed class RunCommand(ISettingsValidator settingsValidator, IQueueService queue, RunConversion conversion, RunWorkerEnd workerEnd)
{
    public Task<int> RunAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken) =>
        workerEnd.GuardAsync(configuration, command, () => ExecuteAsync(configuration, command, cancellationToken));

    private async Task<int> ExecuteAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (ReportInvalidSettings(settingsValidator, configuration)) return (int)ExitCode.InvalidConfiguration;
        using var runLock = workerEnd.TakeRunLock(configuration, LockHolders.Run);
        if (runLock is null) return (int)ExitCode.GeneralFailure;
        await conversion.PrintBannerAsync(configuration, command.ModeOverride, cancellationToken);
        var preparation = await queue.PrepareAsync(configuration.Settings.Database.Path, configuration.Settings, sourceKind: null, modeOverridden: command.ModeOverride is not null, cancellationToken);
        var (ready, _, failures, workerUnreachable, stopped, workerHandled) = await conversion.ProcessPreparedJobsAsync(configuration, command, preparation, (ready, skipped, failures) =>
        {
            Console.WriteLine(OutcomeSummary.Run(ready, skipped, failures.Length, OutputStyle));
            RunConversion.PrintFailedJobs(failures);
            if (ready > 0) Console.WriteLine("Next: review the outputs, then run 'video-optimiser finalize --ready'.");
        }, cancellationToken, prepared => ReadyImmichVideosAsync(configuration, prepared, cancellationToken));
        return stopped ? (int)ExitCode.Cancelled : !workerHandled ? (int)ExitCode.GeneralFailure : (int)QueueExitCode.Calculate(ready, failures.Length, workerUnreachable);
    }

    /// <summary>
    /// R8: after preparation released the finalise lock and before the worker check, the Immich videos either get their
    /// downloaded copies back or stay queued with a line saying why, so the worker is created only for work that can run.
    /// Immich is asked only for a missing copy: converting needs nothing else from it. Folder videos convert either way.
    /// </summary>
    private async Task<QueuePreparationResult> ReadyImmichVideosAsync(LoadedConfiguration configuration, QueuePreparationResult preparation, CancellationToken cancellationToken)
    {
        var immichJobs = preparation.RunnableJobs.Where(job => job.SourceKind == JobSourceKind.Immich).ToArray();
        var foldersOnly = preparation with { RunnableJobs = preparation.RunnableJobs.Where(job => job.SourceKind == JobSourceKind.File).ToArray(), SourceKind = JobSourceKind.File };
        // Without Immich the run takes no Immich video, a joining one included.
        if (!configuration.Settings.Immich.IsConfigured)
        {
            if (immichJobs.Length > 0) Console.WriteLine(RunConversion.ImmichStayQueued(immichJobs.Length, "immich.baseUrl isn't set"));
            return foldersOnly;
        }
        if (immichJobs.Length == 0) return preparation;

        var restage = await conversion.RestageAsync(configuration, immichJobs, cancellationToken);
        var failed = restage.Failed.Select(job => job.Id).ToHashSet();
        var ready = restage.Unusable is null ? preparation : foldersOnly;
        return ready with
        {
            RunnableJobs = ready.RunnableJobs.Where(job => !failed.Contains(job.Id)).ToArray(),
            FailedJobs = [.. preparation.FailedJobs, .. restage.Failed.Select(job => RunConversion.ToFailedQueueJob(job, job.FailureMessage!))]
        };
    }
}
