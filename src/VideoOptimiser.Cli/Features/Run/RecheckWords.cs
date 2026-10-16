using System.Globalization;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// The re-check's two lines (VOPT-119), shared by the console and the TUI's log. The tally is a sentence, not the run's
/// <c>Label: N.</c> tally: the run's own tally can follow a few lines later, and two of the same shape would read as one
/// counted twice.
/// </summary>
internal static class RecheckWords
{
    public static string Started(int count) => count == 1
        ? "Checking the AV1 of 1 video that failed validation earlier."
        : $"Checking the AV1s of {Count(count)} videos that failed validation earlier.";

    /// <param name="tone">Colours one part in its outcome's tone; the TUI, which colours whole lines, passes the part through.</param>
    public static string Tally(int ready, int toCheck, int notWorthConverting, int stillFailed, Func<JobTone, string, string> tone)
    {
        var parts = new List<string>();
        if (ready > 0) parts.Add(tone(JobTone.Success, toCheck > 0 ? $"{Count(ready)} ready to finalise ({Count(toCheck)} to check)" : $"{Count(ready)} ready to finalise"));
        if (notWorthConverting > 0) parts.Add(tone(JobTone.Warning, $"{Count(notWorthConverting)} not worth converting"));
        if (stillFailed > 0) parts.Add(tone(JobTone.Failure, $"{Count(stillFailed)} still failed"));
        return $"Checked again: {string.Join(", ", parts)}.";
    }

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);
}
