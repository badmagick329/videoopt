namespace VideoOptimiser.Cli;

/// <summary>
/// The tally lines that end a run and a finalisation. A part whose count is zero is left out: <c>Ready to finalise: 1. Not
/// worth converting: 0. Failed: 0.</c> made the reader check two zeros to find the one number that mattered.
/// </summary>
internal static class OutcomeSummary
{
    public static string Run(int ready, int notWorthConverting, int failed) =>
        Join("Nothing to finalise.", ("Ready to finalise", ready), ("Not worth converting", notWorthConverting), ("Failed", failed));

    public static string Finalisation(int finalised, int failed) =>
        Join("Nothing was finalised.", ("Finalised", finalised), ("Failed", failed));

    private static string Join(string none, params (string Label, int Count)[] parts)
    {
        var shown = parts.Where(part => part.Count > 0).Select(part => $"{part.Label}: {part.Count}.").ToArray();
        return shown.Length == 0 ? none : string.Join(' ', shown);
    }
}
