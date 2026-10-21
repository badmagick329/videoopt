using Spectre.Console;
using Spectre.Console.Rendering;

namespace VideoOptimiser.Cli.Features.Tui;

internal static partial class TuiView
{
    public static IRenderable? Dialog(TuiModel m)
    {
        if (m.Help) return HelpDialog(m);
        return m.Dialog switch
        {
            TuiChoice choice => ChoiceDialog(m, choice),
            TuiConfirm confirm => ConfirmDialog(m, confirm),
            TuiTextInput input => QueueInputDialog(m, input),
            _ => null
        };
    }

    private static Panel ConfirmDialog(TuiModel m, TuiConfirm confirm)
    {
        var width = DialogWidth(m);

        var prompt = new Paragraph();
        prompt.Append("[y/N]", Theme.Of(Theme.Text, bold: true));
        prompt.Append("  y confirms, any other key cancels", Theme.Of(Theme.Dim));

        var rows = new List<IRenderable>
        {
            new Paragraph(" "),
            new Paragraph(confirm.Question, Theme.Of(Theme.Text, bold: true)),
            new Paragraph(" "),
            new Paragraph(confirm.Detail, Theme.Of(Theme.Soft)),
            new Paragraph(" "),
            prompt,
            new Paragraph(" ")
        };
        return DialogPanel("Confirm", width, rows);
    }

    private static int DialogWidth(TuiModel m) => Math.Clamp(m.Width - 6, 30, 58);

    private static Panel QueueInputDialog(TuiModel m, TuiTextInput input)
    {
        var width = DialogWidth(m);
        var inner = width - 6;
        var file = input.Source == QueueAdditionSource.File;
        var rows = new List<IRenderable>
        {
            new Line().Flex(file ? "Type or paste the file path." : "Count: positive; blank = all", Theme.Of(Theme.Soft)).Build(inner),
            new Paragraph(" "),
            new Line().Flex(InputTail(input.Text, inner - 1) + "▏", Theme.Of(Theme.Text, bold: true)).Build(inner),
            new Paragraph(" "),
            new Line().Flex(input.Problem ?? (file ? "Backspace edits the path." : "All means all eligible videos."), Theme.Of(input.Problem is null ? Theme.Dim : Theme.Held)).Build(inner),
            new Line().Flex("enter add · esc cancel", Theme.Of(Theme.Dim)).Build(inner)
        };
        return DialogPanel(file ? "File path" : "Add from Immich", width, rows);
    }

    /// <summary>The end stays visible while typing a long path; width is in terminal cells, including wide Unicode glyphs.</summary>
    private static string InputTail(string text, int width)
    {
        if (TuiText.Cells(text) <= width) return text;
        var tail = new List<string>();
        var cells = 1; // The ellipsis says a prefix is hidden.
        foreach (var rune in text.EnumerateRunes().Reverse())
        {
            var part = rune.ToString();
            cells += TuiText.Cells(part);
            if (cells > width) break;
            tail.Add(part);
        }
        tail.Reverse();
        return "…" + string.Concat(tail);
    }

    private static Panel DialogPanel(string title, int width, List<IRenderable> rows) => new(new Rows(rows))
    {
        Border = BoxBorder.Rounded,
        BorderStyle = Theme.Of(Theme.Accent),
        Padding = new Padding(2, 0, 2, 0),
        Width = width,
        Header = new PanelHeader($"[bold {Theme.Accent.ToMarkup()}] {Markup.Escape(title)} [/]")
    };

    /// <summary>
    /// Wide enough for the longest key row to read whole in a wide window: the key column (15), the border and padding (6). Taken
    /// from the table, so a reworded key (x's toggle, VOPT-189) never gets cut while the window has room.
    /// </summary>
    private static readonly int HelpWidth = TuiHelp.Groups.SelectMany(group => group.Keys).Max(key => key.What.Length) + 15 + 6;

    /// <summary>
    /// Every key, grouped as the footers group them, in the dialogs' style. A window too short for every row shows what fits:
    /// the per-pane groups drop from the end, and the Anywhere group alone is cut at the window's last row.
    /// </summary>
    private static Panel HelpDialog(TuiModel m)
    {
        var width = Math.Clamp(m.Width - 6, 30, HelpWidth);
        var inner = width - 6;
        var room = Math.Max(1, m.Height - 4);
        const string closing = "Any key closes this.";

        List<IRenderable> Rows(int groups)
        {
            var rows = new List<IRenderable>();
            foreach (var group in TuiHelp.Groups.Take(groups))
            {
                if (rows.Count > 0) rows.Add(new Paragraph(" "));
                rows.Add(new Line().Flex(group.Title, Theme.Of(Theme.Accent, bold: true)).Build(inner));
                foreach (var key in group.Keys)
                    rows.Add(new Line().Left($"{key.Key,-15}", Theme.Of(Theme.Text, bold: true)).Flex(key.What, Theme.Of(Theme.Soft)).Build(inner));
            }
            rows.Add(new Paragraph(closing, Theme.Of(Theme.Dim)));
            return rows;
        }

        var fitting = Enumerable.Range(1, TuiHelp.Groups.Count).Reverse().Select(Rows).FirstOrDefault(rows => rows.Count <= room) ?? [.. Rows(1).Take(room)];
        return DialogPanel("Keys", width, fitting);
    }

    /// <summary>A list to pick from, in the confirmation's style: the cursor row has the selection colour, and each option may carry a dim hint (a price).</summary>
    private static Panel ChoiceDialog(TuiModel m, TuiChoice choice)
    {
        var width = DialogWidth(m);
        var inner = width - 6;
        var rows = new List<IRenderable>
        {
            new Paragraph(" "),
            new Paragraph(choice.Title, Theme.Of(Theme.Text, bold: true)),
            new Paragraph(" "),
            new Paragraph(choice.Detail, Theme.Of(Theme.Soft)),
            new Paragraph(" ")
        };
        for (var i = 0; i < choice.Options.Count; i++)
        {
            var option = choice.Options[i];
            var selected = i == choice.Selected;
            var line = new Line()
                .Left(selected ? "▌ " : "  ", Theme.Of(Theme.Accent))
                .Flex(option.Label, Theme.Of(Theme.Text, bold: selected));
            if (option.Hint.Length > 0) line.Right(option.Hint + " ", Theme.Of(Theme.Dim));
            rows.Add(line.Build(inner, selected ? Theme.Selection : null));
        }
        rows.Add(new Paragraph(" "));
        rows.Add(new Paragraph("↑↓ move · enter choose · esc cancel", Theme.Of(Theme.Dim)));
        rows.Add(new Paragraph(" "));
        return DialogPanel("Choose", width, rows);
    }
}
