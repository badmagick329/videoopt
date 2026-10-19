namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>One key and what it does, in the footers' words; a key that only applies in a state says which.</summary>
internal sealed record HelpKey(string Key, string What);

internal sealed record HelpGroup(string Title, IReadOnlyList<HelpKey> Keys);

/// <summary>
/// The key guide the <c>?</c> overlay draws: every key in <see cref="TuiKeys"/>, grouped by pane as the footers group them. A
/// key that works only in some state (the stop keys while a run is going, the worker keys with a managed worker) says so in
/// its description instead of hiding.
/// </summary>
internal static class TuiHelp
{
    public static IReadOnlyList<HelpGroup> Groups { get; } = [.. TuiKeys.Panes.Select(pane => new HelpGroup(pane.Title, Rows(pane.All)))];

    /// <summary>A key with no description of its own joins the row before it, which then names both (<c>x / X</c>).</summary>
    private static List<HelpKey> Rows(IReadOnlyList<TuiKey> keys)
    {
        var rows = new List<HelpKey>();
        foreach (var key in keys)
        {
            if (key.Help is { } what) rows.Add(new HelpKey(key.GuideName, what));
            else rows[^1] = rows[^1] with { Key = $"{rows[^1].Key} / {key.GuideName}" };
        }
        return rows;
    }
}
