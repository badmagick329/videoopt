using System.Globalization;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>One accent colour (focus only), dim secondary text, one colour per status group.</summary>
internal static class Theme
{
    public static readonly Color Accent = new(0xA7, 0x8B, 0xFA);
    public static readonly Color Text = new(0xE5, 0xE7, 0xEB);
    public static readonly Color Soft = new(0x9C, 0xA3, 0xAF);
    public static readonly Color Dim = new(0x6B, 0x72, 0x80);
    public static readonly Color Faint = new(0x3F, 0x46, 0x55);
    public static readonly Color Selection = new(0x2A, 0x26, 0x4A);

    public static readonly Color Converting = new(0x2D, 0xD4, 0xBF);
    public static readonly Color Queue = new(0x60, 0xA5, 0xFA);
    public static readonly Color Held = new(0xFB, 0xBF, 0x24);
    public static readonly Color Ready = new(0x4A, 0xDE, 0x80);
    public static readonly Color Failed = new(0xF8, 0x71, 0x71);
    public static readonly Color Stopped = new(0xFB, 0x92, 0x3C);

    public static Style Of(Color color, bool bold = false) => new(color, decoration: bold ? Decoration.Bold : Decoration.None);
}

internal static class TuiText
{
    public static string Size(long bytes) => HumanReadableValues.FormatSize(bytes);

    public static string Percent(decimal percent) => percent.ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>EUR with the digits a few minutes of a cheap server needs; the price an hour has three (<c>HourlyPrice</c>), a running cost needs more.</summary>
    public static string Eur(decimal value) => "EUR " + value.ToString("0.0000", CultureInfo.InvariantCulture);

    /// <summary>How many terminal cells a text takes: wide glyphs (CJK, emoji) take two, so file names from real folders still line up.</summary>
    public static int Cells(string text) => text.GetCellWidth();

    /// <summary>Cuts to <paramref name="width"/> cells with an ellipsis.</summary>
    public static string Fit(string text, int width)
    {
        if (width <= 0) return string.Empty;
        if (Cells(text) <= width) return text;
        if (width == 1) return "…";
        var cut = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var cells = Cells(rune.ToString());
            if (used + cells > width - 1) break;
            cut.Append(rune.ToString());
            used += cells;
        }
        return cut.Append('…').ToString();
    }
}

/// <summary>
/// A row of styled runs, a flexible name that gives way first, and right-aligned runs, padded to an exact width. Building rows
/// by hand (instead of a Table) lets a selected row carry a background across its whole width and guarantees no wrapping.
/// </summary>
internal sealed class Line
{
    private readonly List<(string Text, Style Style)> _left = [];
    private readonly List<(string Text, Style Style)> _right = [];
    private (string Text, Style Style)? _flex;

    public Line Left(string text, Style style) { _left.Add((text, style)); return this; }
    public Line Flex(string text, Style style) { _flex = (text, style); return this; }
    public Line Right(string text, Style style) { _right.Add((text, style)); return this; }

    public IRenderable Build(int width, Color? background = null)
    {
        Style Bg(Style s) => background is { } b ? new Style(s.Foreground, b, s.Decoration) : s;

        var fixedLeft = _left.Sum(run => TuiText.Cells(run.Text));
        var rightWidth = _right.Sum(run => TuiText.Cells(run.Text));
        var room = width - fixedLeft - rightWidth - (rightWidth > 0 ? 1 : 0);
        var flexText = TuiText.Fit(_flex?.Text ?? string.Empty, Math.Max(0, room));

        var paragraph = new Paragraph();
        var used = 0;
        foreach (var (text, style) in _left) { paragraph.Append(text, Bg(style)); used += TuiText.Cells(text); }
        if (_flex is { } flex && flexText.Length > 0) { paragraph.Append(flexText, Bg(flex.Style)); used += TuiText.Cells(flexText); }
        var gap = Math.Max(0, width - used - rightWidth);
        if (gap > 0) paragraph.Append(new string(' ', gap), Bg(Style.Plain));
        foreach (var (text, style) in _right) paragraph.Append(text, Bg(style));
        return paragraph;
    }
}

internal static class Bars
{
    /// <summary>The window of <paramref name="capacity"/> rows that keeps <paramref name="selected"/> in view, centred when it can be.</summary>
    public static int WindowStart(int total, int selected, int capacity)
    {
        if (total <= capacity) return 0;
        return Math.Clamp(selected - capacity / 2, 0, total - capacity);
    }
}

/// <summary>Draws a prepared list of lines as a renderable. Spectre has no layers, so the dialog overlay is composed from these.</summary>
internal sealed class SegmentLines(IReadOnlyList<List<Segment>> lines) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            foreach (var segment in lines[i]) yield return segment;
            if (i < lines.Count - 1) yield return Segment.LineBreak;
        }
    }
}

/// <summary>
/// Overlays one renderable on another by splicing segments line by line. The base is dimmed, so the dialog reads as the only
/// live thing on screen.
/// </summary>
internal static class Compositor
{
    public static List<List<Segment>> Lines(IRenderable renderable, IAnsiConsole console)
    {
        var lines = new List<List<Segment>> { new() };
        foreach (var segment in renderable.GetSegments(console))
        {
            // Layout and Panel end their rows with plain newline segments, which IsLineBreak does not flag.
            if (segment.IsLineBreak || segment.Text is "\n" or "\r\n") lines.Add([]);
            else lines[^1].Add(segment);
        }
        return lines;
    }

    public static IRenderable Overlay(IRenderable baseScreen, IRenderable dialog, int screenWidth, int screenHeight, IAnsiConsole console)
    {
        var back = Lines(baseScreen, console);
        var front = Lines(dialog, console);
        while (front.Count > 0 && front[^1].Count == 0) front.RemoveAt(front.Count - 1);
        var dialogWidth = front.Max(line => line.Sum(segment => TuiText.Cells(segment.Text)));
        var x = Math.Max(0, (screenWidth - dialogWidth) / 2);
        var y = Math.Max(0, (screenHeight - front.Count) / 2);

        var result = new List<List<Segment>>();
        for (var row = 0; row < back.Count; row++)
        {
            var dimmed = back[row].Select(Dim).ToList();
            var dialogRow = row - y;
            if (dialogRow < 0 || dialogRow >= front.Count) { result.Add(dimmed); continue; }
            var line = new List<Segment>();
            line.AddRange(Slice(dimmed, 0, x));
            line.AddRange(front[dialogRow]);
            line.AddRange(Slice(dimmed, x + dialogWidth, int.MaxValue));
            result.Add(line);
        }
        return new SegmentLines(result);
    }

    private static Segment Dim(Segment s) =>
        new(s.Text, new Style(s.Style.Foreground, s.Style.Background, s.Style.Decoration | Decoration.Dim));

    /// <summary>The runs between cell columns [from, to), cut by glyph so a wide one is never split.</summary>
    private static IEnumerable<Segment> Slice(List<Segment> line, int from, int to)
    {
        var column = 0;
        foreach (var segment in line)
        {
            var kept = new StringBuilder();
            foreach (var rune in segment.Text.EnumerateRunes())
            {
                var cells = TuiText.Cells(rune.ToString());
                if (column >= from && column + cells <= to) kept.Append(rune.ToString());
                column += cells;
            }
            if (kept.Length > 0) yield return new Segment(kept.ToString(), segment.Style);
        }
    }
}
