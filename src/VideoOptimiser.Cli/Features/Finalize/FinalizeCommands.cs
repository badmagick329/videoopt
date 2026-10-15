using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using static CliLocks;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Finalize;

/// <summary>
/// <c>finalize</c> and <c>immich finalize</c>: replace the originals with their AV1s after the user has reviewed them. Both
/// hold the finalise lock for the whole command, so a run leaves the jobs alone while they are being finalised.
/// </summary>
internal sealed class FinalizeCommands(IJobRepository repository, IFinalizationService finalizer, IImmichClientFactory immichClients)
{
    public async Task<int> FinalizeAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        using var finalizeLock = await TakeFinalizeLockAsync(configuration, "finalize", cancellationToken);
        if (finalizeLock is null) return (int)ExitCode.GeneralFailure;
        if (!command.FinalizeReady)
        {
            if (!Guid.TryParse(command.JobId, out var id)) { WriteError("Job ID is invalid."); return (int)ExitCode.InvalidArguments; }
            var result = await finalizer.FinalizeAsync(configuration.Settings.Database.Path, id, configuration.Settings, cancellationToken);
            if (result.ExitCode == ExitCode.Success) Console.WriteLine(result.Message);
            else WriteError(result.Message);
            return (int)result.ExitCode;
        }

        var ready = (await repository.ListAsync(configuration.Settings.Database.Path, terminal: false, cancellationToken))
            .Where(job => job.Status == JobStatus.ReadyToFinalize || FinalizationRules.IsInterruptedImmich(job) || FinalizationRules.IsInterruptedFile(job))
            .ToArray();
        if (ready.Length == 0) { Console.WriteLine("No jobs are ready to finalise."); return (int)ExitCode.Success; }
        foreach (var job in ready) Console.WriteLine(FinalizationConsole.DescribeReady(job));
        if (ready.Any(job => job.SourceKind == JobSourceKind.File)) Console.WriteLine(DescribeOriginalAction(configuration.Settings.Original));
        if (ready.Any(job => job.SourceKind == JobSourceKind.Immich)) Console.WriteLine("Each Immich original goes to Immich's trash once its AV1 is uploaded with its albums, tags and other relations.");
        Console.Write("Finalise these jobs? [y/N] ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Finalisation cancelled."); return (int)ExitCode.Success; }
        var report = new FinalizationConsole(Console.Out, OutputStyle, Console.Error, ErrorStyle);
        var failed = new List<(JobRecord Job, string Message)>();
        foreach (var job in ready)
        {
            var result = await finalizer.FinalizeAsync(configuration.Settings.Database.Path, job.Id, configuration.Settings, cancellationToken);
            if (!report.Outcome(job, result)) failed.Add((job, result.Message));
        }

        report.Summary(ready.Length, failed);
        return failed.Count == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }

    private static string DescribeOriginalAction(OriginalSettings original) => original.Action.ToLowerInvariant() switch
    {
        OriginalActions.Archive => Path.IsPathFullyQualified(original.ArchiveDirectory)
            ? $"Each original moves to {original.ArchiveDirectory} and its AV1 takes its place."
            : $"Each original moves to an {original.ArchiveDirectory} folder beside it and its AV1 takes its place.",
        OriginalActions.Keep => "Each original stays; its AV1 is installed beside it as <name>.av1<extension>.",
        _ => "Each original is deleted and its AV1 takes its place."
    };

    public async Task<int> ImmichFinalizeAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (!RequireImmich(configuration, command)) return (int)ExitCode.InvalidConfiguration;
        using var finalizeLock = await TakeFinalizeLockAsync(configuration, "immich finalize", cancellationToken);
        if (finalizeLock is null) return (int)ExitCode.GeneralFailure;
        var settings = configuration.Settings;
        var ready =(await repository.ListAsync(settings.Database.Path, terminal: false, cancellationToken))
            .Where(job => job.SourceKind == JobSourceKind.Immich && (job.Status == JobStatus.ReadyToFinalize || FinalizationRules.IsInterruptedImmich(job)))
            .ToArray();
        if (ready.Length == 0) { Console.WriteLine("No Immich jobs are ready to finalise."); return (int)ExitCode.Success; }

        var client = immichClients.Create(settings.Immich);
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
            if (job.FailureCategory == FailureCategories.FinalizationIncomplete) Console.WriteLine($"    Resumes after: {job.FailureMessage}");
        }

        var totalBefore = ready.Sum(job => job.SourceSizeBytes ?? 0);
        var totalAfter = ready.Sum(job => job.OutputSizeBytes ?? 0);
        Console.WriteLine($"{OutputWords.Count(ready.Length, "video", "videos")}: {HumanReadableValues.FormatSize(totalBefore)} -> {HumanReadableValues.FormatSize(totalAfter)}, saving {HumanReadableValues.FormatSize(totalBefore - totalAfter)}.");
        Console.WriteLine("Each AV1 is uploaded as a new asset with the original's albums, tags, favourite and other relations; then the original goes to Immich's trash.");
        Console.Write("Finalise these jobs? [y/N] ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Finalisation cancelled."); return (int)ExitCode.Success; }

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
}
