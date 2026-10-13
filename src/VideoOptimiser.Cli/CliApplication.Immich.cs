using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
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
    private static async Task<int> RunImmichRunAsync(IServiceProvider services, LoadedConfiguration configuration, int? limit, ProcessingModeOverride? modeOverride, CancellationToken cancellationToken)
    {
        if (!RequireImmich(configuration) || ReportInvalidSettings(services, configuration)) return (int)ExitCode.InvalidConfiguration;
        // Discovery downloads originals, so the banner comes first.
        await PrintRunBannerAsync(services, configuration, modeOverride, cancellationToken);
        var settings = configuration.Settings;
        // Guard reports are printed as they arrive and kept: when a later error ends the run, the next run sees the trashed
        // re-uploads as trashed and stays silent, so this run is the only one that can tell the user to delete them.
        var guardReports = new List<ScanProgress>();
        var guardProgress = new InlineProgress<ScanProgress>(update =>
        {
            guardReports.Add(update);
            Console.WriteLine(FormatGuardReport(update));
        });
        var discoveryProgress = new InlineProgress<ScanProgress>(update =>
        {
            if (update.Stage == "Excluded") Console.Error.WriteLine($"EXCLUDED {update.Path}: {update.Message}");
            else Console.WriteLine(FormatScanProgress(update));
        });

        Console.WriteLine("Checking converted originals for re-uploads, then searching Immich for videos to convert.");
        ImmichRunPreparation preparation;
        try
        {
            preparation = await services.GetRequiredService<IImmichRunService>().PrepareAsync(settings.Database.Path, settings, limit, guardProgress, discoveryProgress, modeOverridden: modeOverride is not null, cancellationToken);
        }
        catch when (guardReports.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Before the run ended, the re-upload guard reported:");
            foreach (var report in guardReports) Console.WriteLine(FormatGuardReport(report));
            throw;
        }

        var (guard, discovery) = (preparation.Guard, preparation.Discovery);
        Console.WriteLine($"Re-upload guard: checked {guard.Checked} converted originals. Trashed: {guard.Trashed.Count}. Restored: {guard.Restored.Count}. Issues: {guard.Issues.Count}.");
        if (discovery.Restaged > 0) Console.WriteLine($"Downloaded again: {discovery.Restaged} staged original(s) that open jobs were missing.");
        Console.WriteLine($"Queued: {discovery.Queued.Count}. Already queued: {discovery.AlreadyQueued}. Already converted: {discovery.AlreadyConverted}. Previously failed or cancelled: {discovery.PreviouslyFailed}. Previously skipped: {discovery.PreviouslySkipped}. Ineligible: {discovery.Ineligible}. Skipped: {discovery.Skipped}. Excluded: {discovery.Excluded}. Issues: {discovery.Issues}.");
        if (discovery.Excluded > 0) Console.Error.WriteLine($"Immich's search returned {discovery.Excluded} asset(s) its filter should have left out. They were left alone, but the search may behave differently in this Immich version: check before running again.");

        int ready;
        FailedQueueJob[] failures;
        try
        {
            (ready, failures) = await ProcessPreparedJobsAsync(services, configuration, preparation.Queue, (ready, failures) =>
            {
                Console.WriteLine($"Ready to finalise: {ready}. Failed: {failures.Length}.");
                PrintFailedJobs(failures);
                PrintGuardSummary(guard);
                if (ready > 0) Console.WriteLine("Review the outputs, then run 'video-optimiser immich finalize'.");
            }, cancellationToken);
        }
        catch
        {
            // Ctrl+C or an error during hours of encoding: the guard's lines have long scrolled away.
            PrintGuardSummary(guard);
            throw;
        }

        var exitCode = QueueExitCode.Calculate(ready, failures.Length);
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

            var sizes = job is { SourceSizeBytes: { } before, OutputSizeBytes: { } after } ? $"{HumanReadableValues.FormatSize(before)} → {HumanReadableValues.FormatSize(after)}" : "sizes unknown";
            var saved = job.PercentageSaved is { } percentage ? $", {percentage:F1}% saved" : string.Empty;
            Console.WriteLine($"{job.Id:N}  {Path.GetFileName(job.SourcePath)}  {taken}  {sizes}{saved}");
            if (job.FailureCategory == FinalizationService.IncompleteCategory) Console.WriteLine($"    Resumes after: {job.FailureMessage}");
        }

        var totalBefore = ready.Sum(job => job.SourceSizeBytes ?? 0);
        var totalAfter = ready.Sum(job => job.OutputSizeBytes ?? 0);
        Console.WriteLine($"{ready.Length} job(s): {HumanReadableValues.FormatSize(totalBefore)} → {HumanReadableValues.FormatSize(totalAfter)}, saving {HumanReadableValues.FormatSize(totalBefore - totalAfter)}.");
        Console.WriteLine("Each AV1 is uploaded as a new asset with the original's albums, tags, favourite and other relations; then the original goes to Immich's trash.");
        Console.Write("Finalise these jobs? [y/N] ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Finalisation cancelled."); return (int)ExitCode.Success; }

        var finalizer = services.GetRequiredService<IFinalizationService>();
        var failed = new List<(JobRecord Job, string Message)>();
        foreach (var job in ready)
        {
            var result = await finalizer.FinalizeAsync(settings.Database.Path, job.Id, settings, cancellationToken);
            if (result.ExitCode == ExitCode.Success)
            {
                Console.WriteLine($"Done    {Path.GetFileName(job.SourcePath)}: {result.Message}");
                continue;
            }

            failed.Add((job, result.Message));
            Console.Error.WriteLine($"Failed  {Path.GetFileName(job.SourcePath)}: {result.Message}");
        }

        Console.WriteLine($"Finalised: {ready.Length - failed.Count}. Failed: {failed.Count}.");
        foreach (var (job, message) in failed) Console.WriteLine($"{job.Id:N}  {Path.GetFileName(job.SourcePath)}  {message}");
        return failed.Count == 0 ? (int)ExitCode.Success
            : failed.Count < ready.Length ? (int)ExitCode.PartialSuccess
            : (int)ExitCode.FinalisationFailure;
    }

    private static bool RequireImmich(LoadedConfiguration configuration)
    {
        if (configuration.Settings.Immich.IsConfigured) return true;
        Console.Error.WriteLine("Set immich.baseUrl in the configuration first; 'video-optimiser doctor' then checks the connection and the API key.");
        return false;
    }

    private static string FormatGuardReport(ScanProgress update) =>
        update.Stage == "Issue" ? $"RE-UPLOAD GUARD ISSUE: {update.Message}" : $"{update.Stage.ToUpperInvariant()}: {update.Message}";

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
