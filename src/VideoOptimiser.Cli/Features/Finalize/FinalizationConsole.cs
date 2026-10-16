using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Finalize;

/// <summary>
/// The lines <c>finalize --ready</c> and <c>immich finalize</c> share: what is about to be finalised, one line per job as it
/// ends, and the tally. Jobs are named by file, as in a job's own output, and a failure is printed once.
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

    /// <summary>What finalising does with the originals, as <c>finalize</c> and the TUI's confirmation both say it.</summary>
    internal static string DescribeOriginalAction(OriginalSettings original, IReadOnlyList<JobRecord> fileJobs)
    {
        // The AV1 takes the output container's extension, not the original's: a .mov original is replaced by an .mp4 file. The
        // containers come from the AV1s about to be installed, so the line is true for every original in the list.
        var containers = fileJobs.Select(job => Path.GetExtension(job.OutputPath ?? string.Empty).ToLowerInvariant()).Where(extension => extension.Length > 0).Distinct().Order().ToArray();
        var place = containers.Length == 0 ? "its AV1 takes its place." : $"its AV1 takes its place as an {string.Join(" or ", containers)} file.";
        return original.Action.ToLowerInvariant() switch
        {
            OriginalActions.Archive => Path.IsPathFullyQualified(original.ArchiveDirectory)
                ? $"Each original moves to {original.ArchiveDirectory} and {place}"
                : $"Each original moves to an {original.ArchiveDirectory} folder beside it and {place}",
            OriginalActions.Keep => "Each original stays; its AV1 is installed beside it as <name>.av1<extension>.",
            _ => $"Each original is deleted and {place}"
        };
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

    /// <summary>Only the tally: each failure was printed once, as it happened, with the commands its message names.</summary>
    public void Summary(int total, int failed) => output.WriteLine(OutcomeSummary.Finalisation(total - failed, failed, outputStyle));
}
