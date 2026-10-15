using VideoOptimiser.Application.Features.Cleanup;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli.Features.Cleanup;

/// <summary>What <c>cleanup</c> would delete, folder by folder, so a folder's path is printed once rather than per file.</summary>
internal static class CleanupReport
{
    /// <summary>The count of each kind that is present, then the total; a kind with none is not mentioned.</summary>
    public static string Summary(TemporaryOutputCleanupPlan plan)
    {
        var parts = new List<string>();
        if (plan.Abandoned.Count > 0) parts.Add($"Abandoned temporary outputs: {plan.Abandoned.Count}.");
        if (plan.StagedOriginals.Count > 0) parts.Add($"Staged Immich originals: {plan.StagedOriginals.Count}.");
        parts.Add($"Total: {HumanReadableValues.FormatSize(plan.TotalBytes)}.");
        return string.Join(' ', parts);
    }

    public static void WriteFiles(TextWriter output, TemporaryOutputCleanupPlan plan)
    {
        var files = plan.Abandoned.Select(file => (file.Path, file.SizeBytes, file.Reason))
            .Concat(plan.StagedOriginals.Select(file => (file.Path, file.SizeBytes, file.Reason)));
        foreach (var folder in files.GroupBy(file => Path.GetDirectoryName(file.Path) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine(folder.Key);
            foreach (var file in folder) output.WriteLine($"  {HumanReadableValues.FormatSize(file.SizeBytes),10}  {Path.GetFileName(file.Path)} ({file.Reason})");
        }
    }
}
