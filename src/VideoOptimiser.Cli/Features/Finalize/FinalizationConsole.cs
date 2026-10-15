using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Finalize;

/// <summary>
/// The lines <c>finalize --ready</c> and <c>immich finalize</c> share: what is about to be finalised, one line per job as it
/// ends, and the tally. Jobs are named by file, as in a job's own output; the IDs a retry needs come last, only for failures.
/// </summary>
internal sealed class FinalizationConsole(TextWriter output, TerminalStyle outputStyle, TextWriter error, TerminalStyle errorStyle)
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
        // The word is coloured and padded after, so the file names still line up.
        var label = succeeded ? outputStyle.Green("Done") + "    " : errorStyle.Red("Failed") + "  ";
        (succeeded ? output : error).WriteLine($"{label}{Path.GetFileName(job.SourcePath)}: {result.Message}");
        return succeeded;
    }

    public void Summary(int total, IReadOnlyList<(JobRecord Job, string Message)> failed)
    {
        output.WriteLine(OutcomeSummary.Finalisation(total - failed.Count, failed.Count, outputStyle));
        foreach (var (job, message) in failed) output.WriteLine($"{job.Id:N}  {Path.GetFileName(job.SourcePath)}  {message}");
    }
}
