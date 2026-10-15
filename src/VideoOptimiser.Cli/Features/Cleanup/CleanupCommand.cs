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
        var plan = await cleanup.FindAsync(settings.Database.Path, settings.Immich.StagingDirectory, cancellationToken);
        // "Kept", not "Skipped": Skipped is the name of a job status.
        foreach (var path in plan.UnknownJobFiles) Console.WriteLine($"Kept  {path} (its job is not in this database)");
        if (plan.Count == 0)
        {
            Console.WriteLine("No abandoned temporary outputs or staged Immich originals found.");
            if (dryRun) Console.WriteLine(DryRunEnding);
            return (int)ExitCode.Success;
        }
        CleanupReport.WriteFiles(Console.Out, plan);
        Console.WriteLine(CleanupReport.Summary(plan));
        if (plan.StagedOriginals.Count > 0) Console.WriteLine("Staged Immich originals are local copies of Immich assets. After 'queue retry', 'immich run' downloads the copy again.");
        if (dryRun)
        {
            Console.WriteLine(DryRunEnding);
            return (int)ExitCode.Success;
        }

        Console.Write("Delete these files? [y/N] ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Cleanup cancelled."); return (int)ExitCode.Success; }
        var result = cleanup.Delete(plan);
        foreach (var failure in result.Failures) WriteError($"Could not delete {failure}");
        Console.WriteLine($"Deleted {OutputWords.Count(result.Deleted, "file", "files")}, {HumanReadableValues.FormatSize(result.DeletedBytes)}.");
        return result.Failures.Count == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }
}
