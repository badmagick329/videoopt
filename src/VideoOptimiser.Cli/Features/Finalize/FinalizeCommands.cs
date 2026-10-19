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
            // Only a warned video asks first: a clean one finalises at once, as scripts and the e2e test rely on. An unknown ID
            // falls through, so the service reports it as before.
            if (await repository.GetAsync(configuration.Settings.Database.Path, id, cancellationToken) is { } warned && FinalizationRules.IsLeftForReview(warned)
                && !ConfirmWarned(warned, configuration.Settings.Original))
            {
                CliConsole.WriteLine("Finalisation cancelled.");
                return (int)ExitCode.Success;
            }
            var result = await finalizer.FinalizeAsync(configuration.Settings.Database.Path, id, configuration.Settings, cancellationToken);
            if (result.ExitCode == ExitCode.Success) CliConsole.WriteLine(result.Message);
            else WriteError(result.Message);
            return (int)result.ExitCode;
        }

        var finalisable = (await repository.ListAsync(configuration.Settings.Database.Path, terminal: false, cancellationToken)).Where(FinalizationRules.IsFinalisable).ToArray();
        if (finalisable.Length == 0) { CliConsole.WriteLine("No jobs are ready to finalise."); return (int)ExitCode.Success; }
        var ready = finalisable.Where(FinalizationRules.IsBulkFinalisable).ToArray();
        var leftOut = finalisable.Where(FinalizationRules.IsLeftForReview).ToArray();
        if (ready.Length == 0) return OnlyWarned("every ready video has validation warnings", leftOut, command);
        foreach (var job in ready) CliConsole.WriteLine(FinalizationConsole.DescribeReady(job));
        if (ready.Where(job => job.SourceKind == JobSourceKind.File).ToArray() is { Length: > 0 } fileJobs) CliConsole.WriteLine(FinalizationConsole.DescribeOriginalAction(configuration.Settings.Original, fileJobs));
        if (ready.Any(job => job.SourceKind == JobSourceKind.Immich)) CliConsole.WriteLine(ImmichTrashLine);
        WriteLeftOut("Left out because validation warned about them. Watch each one, then finalise it on its own:", leftOut, command);
        CliConsole.Write("Finalise these jobs? [y/N] ");
        if (!string.Equals(CliConsole.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) { CliConsole.WriteLine("Finalisation cancelled."); return (int)ExitCode.Success; }
        var report = new FinalizationConsole(CliConsole.Out, OutputStyle, CliConsole.Error, ErrorStyle);
        var failed = 0;
        foreach (var job in ready)
        {
            var result = await finalizer.FinalizeAsync(configuration.Settings.Database.Path, job.Id, configuration.Settings, cancellationToken);
            if (!report.Outcome(job, result)) failed++;
        }

        report.Summary(ready.Length, failed);
        WriteLeftOut("Left out with validation warnings:", leftOut, command);
        return BatchExitCode(ready.Length - failed, failed);
    }

    /// <summary>
    /// The exit code of a bulk finalise that went through its whole batch, the same for <c>finalize --ready</c> and
    /// <c>immich finalize</c> so a script reads one outcome one way: all finalised is success, some is partial, none is a
    /// finalisation failure. A batch with nothing to finalise, or answered "no", exits success before it gets here; a
    /// <c>Ctrl+C</c> part-way throws past it, so it exits cancelled whatever the counts.
    /// </summary>
    private static int BatchExitCode(int finalised, int failed) =>
        (int)(failed == 0 ? ExitCode.Success : finalised > 0 ? ExitCode.PartialSuccess : ExitCode.FinalisationFailure);

    private const string ImmichTrashLine = "Each Immich original goes to Immich's trash once its AV1 is uploaded with its albums, tags and other relations.";

    /// <summary>
    /// What a warned video is and what validation said, then the y/N. The warnings are what the user should have watched the AV1
    /// for, so they come right before the question rather than in a separate command.
    /// </summary>
    private static bool ConfirmWarned(JobRecord job, OriginalSettings original)
    {
        CliConsole.WriteLine(FinalizationConsole.DescribeReady(job));
        var warnings = FinalizationConsole.DescribeWarnings(job);
        CliConsole.WriteLine(warnings[0]);
        // The sentences are coloured as validate colours them; the header and indent stay plain.
        foreach (var line in warnings.Skip(1)) CliConsole.WriteLine($"  {OutputStyle.Tone(JobTone.Warning, line.TrimStart())}");
        foreach (var line in FinalizationConsole.DescribeNotes(job)) CliConsole.WriteLine(line);
        CliConsole.WriteLine(job.SourceKind == JobSourceKind.Immich ? ImmichTrashLine : FinalizationConsole.DescribeOriginalAction(original, [job]));
        CliConsole.Write("Finalise it? [y/N] ");
        return string.Equals(CliConsole.ReadLine(), "y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A bulk finalise where every candidate was left out. It succeeds without a prompt: there is nothing to confirm, and each
    /// video's own command is the way on.
    /// </summary>
    private static int OnlyWarned(string why, IReadOnlyList<JobRecord> leftOut, CliCommand command)
    {
        WriteLeftOut($"Nothing to finalise in bulk: {why}. Watch each one, then finalise it on its own:", leftOut, command);
        return (int)ExitCode.Success;
    }

    /// <summary>The warned videos a bulk finalise skipped, each with the command that finalises it alone. Nothing when none were.</summary>
    private static void WriteLeftOut(string header, IReadOnlyList<JobRecord> leftOut, CliCommand command)
    {
        if (leftOut.Count == 0) return;
        CliConsole.WriteLine(header);
        foreach (var job in leftOut) CliConsole.WriteLine($"  {Path.GetFileName(job.SourcePath)}  {command.Hint($"finalize {job.Id:N}")}");
    }

    public async Task<int> ImmichFinalizeAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (!RequireImmich(configuration, command)) return (int)ExitCode.InvalidConfiguration;
        using var finalizeLock = await TakeFinalizeLockAsync(configuration, "immich finalize", cancellationToken);
        if (finalizeLock is null) return (int)ExitCode.GeneralFailure;
        var settings = configuration.Settings;
        var finalisable = (await repository.ListAsync(settings.Database.Path, terminal: false, cancellationToken))
            .Where(job => job.SourceKind == JobSourceKind.Immich && FinalizationRules.IsFinalisable(job))
            .ToArray();
        if (finalisable.Length == 0) { CliConsole.WriteLine("No Immich jobs are ready to finalise."); return (int)ExitCode.Success; }
        var ready = finalisable.Where(FinalizationRules.IsBulkFinalisable).ToArray();
        var leftOut = finalisable.Where(FinalizationRules.IsLeftForReview).ToArray();
        // Says "Immich video": clean file videos may be ready too, and this command does not take them.
        if (ready.Length == 0) return OnlyWarned("every ready Immich video has validation warnings", leftOut, command);

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
            CliConsole.WriteLine($"{job.Id:N}  {Path.GetFileName(job.SourcePath)}  {taken}  {sizes}{saved}");
            if (job.FailureCategory == FailureCategories.FinalizationIncomplete) CliConsole.WriteLine($"    Resumes after: {job.FailureMessage}");
        }

        var totalBefore = ready.Sum(job => job.SourceSizeBytes ?? 0);
        var totalAfter = ready.Sum(job => job.OutputSizeBytes ?? 0);
        CliConsole.WriteLine($"{OutputWords.Count(ready.Length, "video", "videos")}: {HumanReadableValues.FormatSize(totalBefore)} -> {HumanReadableValues.FormatSize(totalAfter)}, saving {HumanReadableValues.FormatSize(totalBefore - totalAfter)}.");
        CliConsole.WriteLine("Each AV1 is uploaded as a new asset with the original's albums, tags, favourite and other relations; then the original goes to Immich's trash.");
        WriteLeftOut("Left out because validation warned about them. Watch each one, then finalise it on its own:", leftOut, command);
        CliConsole.Write("Finalise these jobs? [y/N] ");
        if (!string.Equals(CliConsole.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) { CliConsole.WriteLine("Finalisation cancelled."); return (int)ExitCode.Success; }

        var report = new FinalizationConsole(CliConsole.Out, OutputStyle, CliConsole.Error, ErrorStyle);
        var failed = 0;
        foreach (var job in ready)
        {
            var result = await finalizer.FinalizeAsync(settings.Database.Path, job.Id, settings, cancellationToken);
            if (!report.Outcome(job, result)) failed++;
        }

        report.Summary(ready.Length, failed);
        if (failed == 0) await PrintTrashRetentionAsync(client, cancellationToken);
        // Last, so what is still to do is the last thing the user reads.
        WriteLeftOut("Left out with validation warnings:", leftOut, command);
        return BatchExitCode(ready.Length - failed, failed);
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
            CliConsole.WriteLine($"Immich keeps the originals in its trash for {OutputWords.Count(trash.Days, "day", "days")}; space comes back when it empties.");
        }
        catch (ImmichApiException exception) when (exception.Code == "ImmichRequestFailed")
        {
        }
    }
}
