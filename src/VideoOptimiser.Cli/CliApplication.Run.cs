using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using static CliLocks;
using static CliOutput;

/// <summary>
/// <c>run</c> (VOPT-54 step 5): converts every queued video, folder and Immich alike, taking each next one from the queue as
/// it is then. Changed wording goes through the humanizer gate (docs index).
/// </summary>
internal static partial class CliApplication
{
    private static async Task<int> RunRunAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (ReportInvalidSettings(services.GetRequiredService<ISettingsValidator>(), configuration)) return (int)ExitCode.InvalidConfiguration;
        using var runLock = TakeRunLock(services, configuration, LockHolders.Run);
        if (runLock is null) return (int)ExitCode.GeneralFailure;
        await PrintRunBannerAsync(services, configuration, command.ModeOverride, cancellationToken);
        var preparation = await services.GetRequiredService<IQueueService>().PrepareAsync(configuration.Settings.Database.Path, configuration.Settings, sourceKind: null, modeOverridden: command.ModeOverride is not null, cancellationToken);
        var (ready, _, failures, workerUnreachable, stopped, workerHandled) = await ProcessPreparedJobsAsync(services, configuration, command, preparation, (ready, skipped, failures) =>
        {
            Console.WriteLine(OutcomeSummary.Run(ready, skipped, failures.Length, OutputStyle));
            PrintFailedJobs(failures);
            if (ready > 0) Console.WriteLine("Next: review the outputs, then run 'video-optimiser finalize --ready'.");
        }, cancellationToken, prepared => ReadyImmichVideosAsync(services, configuration, prepared, cancellationToken));
        return stopped ? (int)ExitCode.Cancelled : !workerHandled ? (int)ExitCode.GeneralFailure : (int)QueueExitCode.Calculate(ready, failures.Length, workerUnreachable);
    }

    /// <summary>
    /// R8: after preparation released the finalise lock and before the worker check, the Immich videos either get their
    /// downloaded copies back or stay queued with a line saying why, so the worker is created only for work that can run.
    /// Immich is asked only for a missing copy: converting needs nothing else from it. Folder videos convert either way.
    /// </summary>
    private static async Task<QueuePreparationResult> ReadyImmichVideosAsync(IServiceProvider services, LoadedConfiguration configuration, QueuePreparationResult preparation, CancellationToken cancellationToken)
    {
        var immichJobs = preparation.RunnableJobs.Where(job => job.SourceKind == JobSourceKind.Immich).ToArray();
        var foldersOnly = preparation with { RunnableJobs = preparation.RunnableJobs.Where(job => job.SourceKind == JobSourceKind.File).ToArray(), SourceKind = JobSourceKind.File };
        // Without Immich the run takes no Immich video, a joining one included.
        if (!configuration.Settings.Immich.IsConfigured)
        {
            if (immichJobs.Length > 0) Console.WriteLine(ImmichStayQueued(immichJobs.Length, "immich.baseUrl isn't set"));
            return foldersOnly;
        }
        if (immichJobs.Length == 0) return preparation;

        var restage = await RestageAsync(services, configuration, immichJobs, cancellationToken);
        var failed = restage.Failed.Select(job => job.Id).ToHashSet();
        var ready = restage.Unusable is null ? preparation : foldersOnly;
        return ready with
        {
            RunnableJobs = ready.RunnableJobs.Where(job => !failed.Contains(job.Id)).ToArray(),
            FailedJobs = [.. preparation.FailedJobs, .. restage.Failed.Select(job => ToFailedQueueJob(job, job.FailureMessage!))]
        };
    }

    /// <summary>
    /// Downloads again the missing copies of <paramref name="jobs"/>, printing today's restaging lines. When Immich can't be
    /// used, says how many of them stay queued: those it did not fail before it stopped answering.
    /// </summary>
    private static async Task<ImmichRestageResult> RestageAsync(IServiceProvider services, LoadedConfiguration configuration, JobRecord[] jobs, CancellationToken cancellationToken)
    {
        var progress = new ImmichDiscoveryConsole(Console.Out, OutputStyle, Console.Error, ErrorStyle);
        var restage = await services.GetRequiredService<IImmichDiscoveryService>().RestageMissingAsync(configuration.Settings.Database.Path, configuration.Settings, jobs, progress, cancellationToken);
        if (restage.Unusable is { } reason)
        {
            progress.FinishLine();
            Console.WriteLine(ImmichStayQueued(jobs.Length - restage.Failed.Count, $"Immich can't be used ({reason})"));
        }
        return restage;
    }

    private static string ImmichStayQueued(int count, string reason) =>
        count == 1 ? $"1 Immich video stays queued: {reason}." : $"{count} Immich videos stay queued: {reason}.";
}
