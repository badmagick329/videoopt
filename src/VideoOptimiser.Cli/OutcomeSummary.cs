using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Cli;

/// <summary>
/// The tally lines that end a run and a finalisation. A part whose count is zero is left out: <c>Ready to finalise: 1. Not
/// worth converting: 0. Failed: 0.</c> made the reader check two zeros to find the one number that mattered. Each part
/// carries the colour its outcome has in a job's own block, so a failure is red wherever it appears.
/// </summary>
internal static class OutcomeSummary
{
    /// <param name="othersReady">Earlier runs left videos ready to finalise. "Nothing to finalise." would then be false, so a run that
    /// made nothing has no tally (null) and the line that names those videos ends the run instead.</param>
    /// <param name="toCheck">Of <paramref name="ready"/>, the videos whose validation left warnings to check.</param>
    public static string? Run(int ready, int notWorthConverting, int failed, TerminalStyle style, bool othersReady = false, int toCheck = 0) =>
        Join(othersReady ? null : "Nothing to finalise.", style, ("Ready to finalise", ready, JobTone.Success, toCheck > 0 ? $" ({toCheck} to check)" : string.Empty), ("Not worth converting", notWorthConverting, JobTone.Warning, string.Empty), ("Failed", failed, JobTone.Failure, string.Empty));

    public static string Finalisation(int finalised, int failed, TerminalStyle style) =>
        Join("Nothing was finalised.", style, ("Finalised", finalised, JobTone.Success, string.Empty), ("Failed", failed, JobTone.Failure, string.Empty))!;

    private static string? Join(string? none, TerminalStyle style, params (string Label, int Count, JobTone Tone, string Note)[] parts)
    {
        var shown = parts.Where(part => part.Count > 0).Select(part => style.Tone(part.Tone, $"{part.Label}: {part.Count}{part.Note}.")).ToArray();
        return shown.Length == 0 ? none : string.Join(' ', shown);
    }
}
