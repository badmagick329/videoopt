namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>Replays keys through the pure update without running effects, so dialogs can be inspected from a shell.</summary>
internal static class SnapshotKeys
{
    /// <remarks>
    /// Yields messages rather than bare keys: <c>newline</c> is an Enter with more input buffered behind it, the line break
    /// inside a paste that the path box keeps, which no <see cref="ConsoleKeyInfo"/> can say (VOPT-190).
    /// </remarks>
    public static bool TryParse(string text, out IReadOnlyList<KeyMsg> keys)
    {
        var parsed = new List<KeyMsg>();
        foreach (var token in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (token.Length == 1)
            {
                var character = token[0];
                var letter = char.IsAsciiLetter(character);
                parsed.Add(new KeyMsg(new ConsoleKeyInfo(character, letter ? (ConsoleKey)char.ToUpperInvariant(character) : default, char.IsAsciiLetterUpper(character), false, false)));
                continue;
            }
            if (token.Equals("newline", StringComparison.OrdinalIgnoreCase))
            {
                parsed.Add(new KeyMsg(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), MoreBuffered: true));
                continue;
            }
            var key = token.ToLowerInvariant() switch
            {
                "up" => ConsoleKey.UpArrow,
                "down" => ConsoleKey.DownArrow,
                "enter" => ConsoleKey.Enter,
                "esc" => ConsoleKey.Escape,
                "backspace" => ConsoleKey.Backspace,
                "tab" => ConsoleKey.Tab,
                _ => (ConsoleKey?)null
            };
            if (key is null)
            {
                keys = [];
                return false;
            }
            parsed.Add(new KeyMsg(new ConsoleKeyInfo('\0', key.Value, false, false, false)));
        }
        keys = parsed;
        return true;
    }
}
