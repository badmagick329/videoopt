using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// Turns a model into one string that repaints the whole screen. Spectre renders into a StringWriter at the terminal's size, so
/// a frame is built off screen and written with a single call: the terminal never sees a half-drawn screen. There is no
/// clear-screen per frame; the frame starts with cursor-home and overwrites every cell, so nothing blinks.
/// </summary>
internal sealed partial class FrameRenderer : IDisposable
{
    private const char Esc = '\u001b';
    private readonly StringWriter _writer = new();
    private readonly IAnsiConsole _console;
    private string? _last;
    private (int Width, int Height) _lastSize;

    public FrameRenderer()
    {
        _console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(_writer)
        });
        _console.Profile.Encoding = Encoding.UTF8;
        _console.Profile.Capabilities.Unicode = true;
    }

    public void Dispose() => _writer.Dispose();

    /// <summary>The bytes to write for this model, or null when the screen would not change.</summary>
    public string? Frame(TuiModel m)
    {
        _console.Profile.Width = m.Width;
        _console.Profile.Height = m.Height;

        var screen = TuiView.Screen(m);
        var dialog = TuiView.Dialog(m);
        var root = dialog is null ? screen : Compositor.Overlay(screen, dialog, m.Width, m.Height, _console);

        _writer.GetStringBuilder().Clear();
        _console.Write(root);
        var rows = _writer.ToString().Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');

        var frame = new StringBuilder();
        // Synchronised-output markers: a terminal that supports them (Windows Terminal does) shows the frame atomically; one
        // that doesn't ignores them.
        frame.Append(Esc).Append("[?2026h");
        // Only a size change (or the first frame) clears the screen, to drop what a different size left behind.
        if (_lastSize != (m.Width, m.Height)) frame.Append(Esc).Append("[2J");
        frame.Append(Esc).Append("[H");
        for (var i = 0; i < m.Height; i++)
        {
            var row = i < rows.Length ? rows[i] : string.Empty;
            frame.Append(row);
            // Every row is padded to the full width, so no erase-to-end-of-line is needed (it would also eat the last cell of a
            // full row).
            var visible = TuiText.Cells(AnsiPattern().Replace(row, string.Empty));
            if (visible < m.Width) frame.Append(Esc).Append("[0m").Append(' ', m.Width - visible);
            if (i < m.Height - 1) frame.Append("\r\n");
        }
        frame.Append(Esc).Append("[0m").Append(Esc).Append("[?2026l");

        _lastSize = (m.Width, m.Height);
        var text = frame.ToString();
        if (text == _last) return null;
        _last = text;
        return text;
    }

    /// <summary>The frame without its escape sequences, one text row per line: what a person or a test reads.</summary>
    public static string Plain(string frame) =>
        AnsiPattern().Replace(frame, string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);

    [GeneratedRegex("\u001b\\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiPattern();
}
