using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.Cli;

/// <summary>
/// The tally lines that end a run and a finalisation. A part whose count is zero is left out: <c>Ready to finalise: 1. Not
/// worth converting: 0. Failed: 0.</c> made the reader check two zeros to find the one number that mattered. Each part
/// carries the colour its outcome has in a job's own block, so a failure is red wherever it appears.
/// </summary>
internal static class OutcomeSummary
{
    public static string Run(int ready, int notWorthConverting, int failed, TerminalStyle style) =>
        Join("Nothing to finalise.", style, ("Ready to finalise", ready, JobTone.Success), ("Not worth converting", notWorthConverting, JobTone.Warning), ("Failed", failed, JobTone.Failure));

    public static string Finalisation(int finalised, int failed, TerminalStyle style) =>
        Join("Nothing was finalised.", style, ("Finalised", finalised, JobTone.Success), ("Failed", failed, JobTone.Failure));

    private static string Join(string none, TerminalStyle style, params (string Label, int Count, JobTone Tone)[] parts)
    {
        var shown = parts.Where(part => part.Count > 0).Select(part => style.Tone(part.Tone, $"{part.Label}: {part.Count}.")).ToArray();
        return shown.Length == 0 ? none : string.Join(' ', shown);
    }
}
