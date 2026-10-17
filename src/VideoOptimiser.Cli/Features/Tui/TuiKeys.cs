namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// The keys of an action that may answer to more than one, kept as one set that <see cref="TuiUpdate"/> and <see cref="TuiHelp"/>
/// both read, so adding a key is one edit here (the user's call, 2026-10-17). Keys match on <see cref="ConsoleKeyInfo.KeyChar"/>,
/// so a character works on any layout that can type it.
/// </summary>
internal static class TuiKeys
{
    /// <summary>Opens and closes the cost page (epic VOPT-126).</summary>
    public static IReadOnlyList<char> CostPage { get; } = ['$'];

    /// <summary>The set as the key guide and the footer name it, such as <c>$</c> or <c>$ / m</c>.</summary>
    public static string Name(IReadOnlyList<char> keys) => string.Join(" / ", keys);
}
