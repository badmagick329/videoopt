using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;
using static CliLocks;
using static CliOutput;

/// <summary>
/// The <c>immich</c> commands. Each round runs by hand: <c>immich run</c> converts, the user reviews, <c>immich finalize</c>
/// replaces the originals in Immich.
/// </summary>
internal static partial class CliApplication
{
    private static async Task<int> RunImmichRunAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (!RequireImmich(configuration) || ReportInvalidSettings(services.GetRequiredService<ISettingsValidator>(), configuration)) return (int)ExitCode.InvalidConfiguration;
        // Before discovery, which marks active jobs interrupted and queues new ones.
        using var runLock = TakeRunLock(services, configuration, LockHolders.ImmichRun);
        if (runLock is null) return (int)ExitCode.GeneralFailure;
        var (limit, modeOverride) = (command.Limit, command.ModeOverride);
        // Discovery downloads originals, so the banner comes first.
        await PrintRunBannerAsync(services, configuration, modeOverride, cancellationToken);
        var settings = configuration.Settings;
        // Guard reports are printed as they arrive and kept: when a later error ends the run, the next run sees the trashed
        // re-uploads as trashed and stays silent, so this run is the only one that can tell the user to delete them.
        var guardReports = new List<ScanProgress>();
        var guardProgress = new InlineProgress<ScanProgress>(update =>
        {
            guardReports.Add(update);
            Console.WriteLine(ReuploadGuardReport.FormatProgress(update));
        });
        var discoveryProgress = new ImmichDiscoveryConsole(Console.Out, OutputStyle, Console.Error, ErrorStyle);

        ImmichRunPreparation preparation;
        try
        {
            preparation = await services.GetRequiredService<IImmichRunService>().PrepareAsync(settings.Database.Path, settings, limit, guardProgress, discoveryProgress, modeOverridden: modeOverride is not null, cancellationToken);
        }
        catch
        {
            // A download that Ctrl+C or an error cut short leaves its line open; the message that follows needs its own.
            discoveryProgress.FinishLine();
            if (guardReports.Count == 0) throw;
            Console.WriteLine();
            Console.WriteLine("Before the run ended, the re-upload guard reported:");
            foreach (var report in guardReports) Console.WriteLine(ReuploadGuardReport.FormatProgress(report));
            throw;
        }

        var (guard, discovery) = (preparation.Guard, preparation.Discovery);
        if (ReuploadGuardReport.Summary(guard) is { } guardLine) Console.WriteLine(guardLine);
        Console.WriteLine();
        foreach (var line in ImmichDiscoveryConsole.Summary(discovery, limit)) Console.WriteLine(line);
        if (discovery.Excluded > 0) WriteError($"Immich's search returned {OutputWords.Count(discovery.Excluded, "asset", "assets")} its filter should have left out. They were left alone, but the search may behave differently in this Immich version: check before running again.");

        int ready;
        FailedQueueJob[] failures;
        bool workerUnreachable;
        bool stopped;
        bool workerHandled;
        try
        {
            (ready, _, failures, workerUnreachable, stopped, workerHandled) = await ProcessPreparedJobsAsync(services, configuration, command, preparation.Queue, (ready, skipped, failures) =>
            {
                Console.WriteLine(OutcomeSummary.Run(ready, skipped, failures.Length, OutputStyle));
                PrintFailedJobs(failures);
                PrintGuardSummary(guard);
                if (ready > 0) Console.WriteLine("Next: review the outputs, then run 'video-optimiser immich finalize'.");
            }, cancellationToken);
        }
        catch
        {
            // Ctrl+C or an error during hours of encoding: the guard's lines have long scrolled away.
            PrintGuardSummary(guard);
            throw;
        }

        if (stopped) return (int)ExitCode.Cancelled;
        if (!workerHandled) return (int)ExitCode.GeneralFailure;
        var exitCode = QueueExitCode.Calculate(ready, failures.Length, workerUnreachable);
        // Guard hits are the guard working; its issues, discovery issues and exclusions each need the user to look.
        if (exitCode == ExitCode.Success && (guard.Issues.Count > 0 || discovery.Issues > 0 || discovery.Excluded > 0)) exitCode = ExitCode.PartialSuccess;
        return (int)exitCode;
    }

    private static void PrintGuardSummary(ImmichReuploadGuardResult guard)
    {
        if (guard.Trashed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("The phone uploaded these converted originals again. They are back in Immich's trash; delete them from the phone:");
            foreach (var hit in guard.Trashed) Console.WriteLine($"  {hit.OriginalFileName}  taken {hit.TakenLocal:yyyy-MM-dd HH:mm}");
        }

        if (guard.Restored.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("These converted originals are out of Immich's trash, so both versions exist. The guard left them alone:");
            foreach (var hit in guard.Restored) Console.WriteLine($"  {hit.OriginalFileName}  taken {hit.TakenLocal:yyyy-MM-dd HH:mm}");
        }

        if (guard.Issues.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Re-upload guard issues:");
            foreach (var issue in guard.Issues) Console.WriteLine($"  {issue.Reason}");
        }
    }
}
