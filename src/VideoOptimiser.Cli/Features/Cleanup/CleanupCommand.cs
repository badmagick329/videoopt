using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Cleanup;
using VideoOptimiser.Domain;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Cleanup;

/// <summary><c>cleanup [--dry-run]</c>: lists what it would delete, then deletes it only after a typed <c>y</c>.</summary>
internal sealed class CleanupCommand(ITemporaryOutputCleanupService cleanup)
{
    private const string DryRunEnding = "Dry run: nothing was deleted.";

    public async Task<int> RunAsync(LoadedConfiguration configuration, bool dryRun, CancellationToken cancellationToken)
    {
        var settings = configuration.Settings;
        // The staging folder is searched whether or not Immich is turned on: a user who emptied immich.baseUrl still has the
        // downloaded copies, and "none found" would be false.
        var plan = await cleanup.FindAsync(settings.Database.Path, settings.Immich, cancellationToken);
        // "Kept", not "Skipped": Skipped is the name of a job status.
        foreach (var path in plan.UnknownJobFiles) CliConsole.WriteLine($"Kept  {path} (its job is not in this database)");
        foreach (var file in plan.Kept) CliConsole.WriteLine($"Kept  {file.Path} ({CleanupReport.KeptReason(file)})");
        if (plan.Count == 0)
        {
            CliConsole.WriteLine("No abandoned temporary outputs or staged Immich originals found.");
            if (dryRun) CliConsole.WriteLine(DryRunEnding);
            return (int)ExitCode.Success;
        }
        CleanupReport.WriteFiles(CliConsole.Out, plan);
        CliConsole.WriteLine(CleanupReport.Summary(plan));
        if (plan.StagedOriginals.Count > 0) CliConsole.WriteLine("Staged Immich originals are local copies of Immich assets. After 'queue retry', 'immich run' downloads the copy again.");
        if (dryRun)
        {
            CliConsole.WriteLine(DryRunEnding);
            return (int)ExitCode.Success;
        }

        CliConsole.Write("Delete these files? [y/N] ");
        if (!string.Equals(CliConsole.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) { CliConsole.WriteLine("Cleanup cancelled."); return (int)ExitCode.Success; }
        // No lock is held across the prompt, so the delete reads the jobs again and leaves any whose status moved meanwhile.
        var result = await cleanup.DeleteAsync(settings.Database.Path, plan, cancellationToken);
        foreach (var path in result.JobChanged) CliConsole.WriteLine($"Kept  {path} (its job changed while cleanup waited for your answer)");
        foreach (var failure in result.Failures) WriteError($"Could not delete {failure}");
        CliConsole.WriteLine($"Deleted {OutputWords.Count(result.Deleted, "file", "files")}, {HumanReadableValues.FormatSize(result.DeletedBytes)}.");
        return result.Failures.Count == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }
}
