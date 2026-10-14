using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Cli;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Jobs;

/// <summary>
/// The <c>immich</c> commands. Each round runs by hand: <c>immich run</c> converts, the user reviews, <c>immich finalize</c>
/// replaces the originals in Immich.
/// </summary>
internal static partial class CliApplication
{
    private static async Task<int> RunImmichRunAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (!RequireImmich(configuration) || ReportInvalidSettings(services, configuration)) return (int)ExitCode.InvalidConfiguration;
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
        try
        {
            (ready, _, failures, workerUnreachable) = await ProcessPreparedJobsAsync(services, configuration, command, preparation.Queue, (ready, skipped, failures) =>
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

        var exitCode = QueueExitCode.Calculate(ready, failures.Length, workerUnreachable);
        // Guard hits are the guard working; its issues, discovery issues and exclusions each need the user to look.
        if (exitCode == ExitCode.Success && (guard.Issues.Count > 0 || discovery.Issues > 0 || discovery.Excluded > 0)) exitCode = ExitCode.PartialSuccess;
        return (int)exitCode;
    }

    private static async Task<int> RunImmichFinalizeAsync(IServiceProvider services, LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!RequireImmich(configuration)) return (int)ExitCode.InvalidConfiguration;
        var settings = configuration.Settings;
        var ready = (await services.GetRequiredService<IJobRepository>().ListAsync(settings.Database.Path, terminal: false, cancellationToken))
            .Where(job => job.SourceKind == JobSourceKind.Immich && (job.Status == JobStatus.ReadyToFinalize || FinalizationService.IsInterruptedImmichFinalization(job)))
            .ToArray();
        if (ready.Length == 0) { Console.WriteLine("No Immich jobs are ready to finalise."); return (int)ExitCode.Success; }

        var client = services.GetRequiredService<IImmichClientFactory>().Create(settings.Immich);
        // Before the prompt, so the user is not asked to confirm a finalisation every job would refuse.
        await ImmichTrash.RequireOnAsync(client, cancellationToken);
        // The capture date is how the user finds each video in Immich to compare before confirming.
        foreach (var job in ready)
        {
            string taken;
            try
            {
                var asset = await client.GetAssetAsync(job.ImmichAssetId!, cancellationToken);
                taken = $"taken {asset.LocalDateTime.DateTime:yyyy-MM-dd HH:mm}{(asset.IsTrashed ? ", in Immich's trash" : string.Empty)}";
            }
            catch (ImmichApiException exception) when (exception.Code == "ImmichRequestFailed")
            {
                taken = "not readable in Immich";
            }

            var sizes = job is { SourceSizeBytes: { } before, OutputSizeBytes: { } after } ? $"{HumanReadableValues.FormatSize(before)} -> {HumanReadableValues.FormatSize(after)}" : "sizes unknown";
            var saved = job.PercentageSaved is { } percentage ? $", {percentage:F1}% saved" : string.Empty;
            Console.WriteLine($"{job.Id:N}  {Path.GetFileName(job.SourcePath)}  {taken}  {sizes}{saved}");
            if (job.FailureCategory == FinalizationService.IncompleteCategory) Console.WriteLine($"    Resumes after: {job.FailureMessage}");
        }

        var totalBefore = ready.Sum(job => job.SourceSizeBytes ?? 0);
        var totalAfter = ready.Sum(job => job.OutputSizeBytes ?? 0);
        Console.WriteLine($"{OutputWords.Count(ready.Length, "video", "videos")}: {HumanReadableValues.FormatSize(totalBefore)} -> {HumanReadableValues.FormatSize(totalAfter)}, saving {HumanReadableValues.FormatSize(totalBefore - totalAfter)}.");
        Console.WriteLine("Each AV1 is uploaded as a new asset with the original's albums, tags, favourite and other relations; then the original goes to Immich's trash.");
        Console.Write("Finalise these jobs? [y/N] ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Finalisation cancelled."); return (int)ExitCode.Success; }

        var finalizer = services.GetRequiredService<IFinalizationService>();
        var report = new FinalizationConsole(Console.Out, OutputStyle, Console.Error, ErrorStyle);
        var failed = new List<(JobRecord Job, string Message)>();
        foreach (var job in ready)
        {
            var result = await finalizer.FinalizeAsync(settings.Database.Path, job.Id, settings, cancellationToken);
            if (!report.Outcome(job, result)) failed.Add((job, result.Message));
        }

        report.Summary(ready.Length, failed);
        if (failed.Count == 0) await PrintTrashRetentionAsync(client, cancellationToken);
        return failed.Count == 0 ? (int)ExitCode.Success
            : failed.Count < ready.Length ? (int)ExitCode.PartialSuccess
            : (int)ExitCode.FinalisationFailure;
    }

    private static bool RequireImmich(LoadedConfiguration configuration)
    {
        if (configuration.Settings.Immich.IsConfigured) return true;
        WriteError("Set immich.baseUrl in the configuration first; 'video-optimiser doctor' then checks the connection and the API key.");
        return false;
    }

    /// <summary>
    /// Tells the user when the originals stop being recoverable and when the space comes back. Left out when Immich does not
    /// answer: the finalisation is done, and this line only explains.
    /// </summary>
    private static async Task PrintTrashRetentionAsync(IImmichClient client, CancellationToken cancellationToken)
    {
        try
        {
            var trash = await client.GetTrashSettingsAsync(cancellationToken);
            Console.WriteLine($"Immich keeps the originals in its trash for {OutputWords.Count(trash.Days, "day", "days")}; space comes back when it empties.");
        }
        catch (ImmichApiException exception) when (exception.Code == "ImmichRequestFailed")
        {
        }
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
