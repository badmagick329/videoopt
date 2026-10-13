using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli;

/// <summary>
/// The lines <c>finalize --ready</c> and <c>immich finalize</c> share: what is about to be finalised, one line per job as it
/// ends, and the tally. Jobs are named by file, as in a job's own output; the IDs a retry needs come last, only for failures.
/// </summary>
internal sealed class FinalizationConsole(TextWriter output, TextWriter error)
{
    /// <summary><c>name  1.0 GiB -> 400.0 MiB, 62.5% saved</c></summary>
    public static string DescribeReady(JobRecord job)
    {
        var sizes = job is { SourceSizeBytes: { } before, OutputSizeBytes: { } after } ? $"{HumanReadableValues.FormatSize(before)} -> {HumanReadableValues.FormatSize(after)}" : "sizes unknown";
        var saved = job.PercentageSaved is { } percentage ? $", {percentage:F1}% saved" : string.Empty;
        return $"{Path.GetFileName(job.SourcePath)}  {sizes}{saved}";
    }

    /// <summary>Whether the job finalised. A failure goes to the error stream, as the exit code says it is one.</summary>
    public bool Outcome(JobRecord job, FinalizationResult result)
    {
        var succeeded = result.ExitCode == ExitCode.Success;
        (succeeded ? output : error).WriteLine($"{(succeeded ? "Done   " : "Failed ")} {Path.GetFileName(job.SourcePath)}: {result.Message}");
        return succeeded;
    }

    public void Summary(int total, IReadOnlyList<(JobRecord Job, string Message)> failed)
    {
        output.WriteLine(OutcomeSummary.Finalisation(total - failed.Count, failed.Count));
        foreach (var (job, message) in failed) output.WriteLine($"{job.Id:N}  {Path.GetFileName(job.SourcePath)}  {message}");
    }
}

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
