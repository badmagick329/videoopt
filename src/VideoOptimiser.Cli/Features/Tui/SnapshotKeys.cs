namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>Replays keys through the pure update without running effects, so dialogs can be inspected from a shell.</summary>
internal static class SnapshotKeys
{
    public static bool TryParse(string text, out IReadOnlyList<ConsoleKeyInfo> keys)
    {
        var parsed = new List<ConsoleKeyInfo>();
        foreach (var token in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (token.Length == 1)
            {
                var character = token[0];
                var letter = char.IsAsciiLetter(character);
                parsed.Add(new ConsoleKeyInfo(character, letter ? (ConsoleKey)char.ToUpperInvariant(character) : default, char.IsAsciiLetterUpper(character), false, false));
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
            parsed.Add(new ConsoleKeyInfo('\0', key.Value, false, false, false));
        }
        keys = parsed;
        return true;
    }
}
