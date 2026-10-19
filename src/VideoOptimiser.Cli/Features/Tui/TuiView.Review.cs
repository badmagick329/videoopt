using Spectre.Console;
using Spectre.Console.Rendering;

namespace VideoOptimiser.Cli.Features.Tui;

internal static partial class TuiView
{
    private static (string Key, string What)[] ReviewHints((string Key, string What)[] stop, (string Key, string What)[] stopFinalise)
    {
        var keys = TuiKeys.Review;
        return [keys.Select.Hint, keys.OpenOriginal.Hint, keys.OpenAv1.Hint, keys.Finalise.Hint, keys.FinaliseAll.Hint, keys.Reject.Hint, .. stopFinalise, .. stop, .. Closing()];
    }

    private static List<IRenderable> ReviewRows(TuiModel m, int width, int capacity, bool focused)
    {
        var selectedRow = 0;
        var ready = m.Ready();
        var rows = new List<IRenderable>();
        var sizes = width >= 52;

        // Column heads, then the list, then the selected video's validation warnings and a summary, both pinned to the bottom.
        // The summary and heads give way on short panes; the warnings take only what the list can spare.
        var showHead = capacity >= 4;
        var showSummary = capacity >= 5;
        var listCapacity = capacity - (showHead ? 1 : 0) - (showSummary ? 1 : 0);
        var chosen = ready.FirstOrDefault(video => video.Id == m.ReviewSelection) ?? ready.FirstOrDefault();
        var warnings = WarningRows(chosen, width, listCapacity - Math.Min(Math.Max(1, ready.Count), ListRowsKept));
        listCapacity -= warnings.Count;
        var notes = NoteRows(chosen, width, listCapacity - Math.Min(Math.Max(1, ready.Count), ListRowsKept));
        listCapacity -= notes.Count;

        // While a finalise is out the Saved cell may hold a tag, so it widens to fit one, head and all, and Original and AV1
        // stay over their figures.
        var savedWidth = m.Finalising is null ? 6 : 10;
        string Saved(string text) => text.PadLeft(savedWidth) + " ";

        if (showHead)
        {
            var head = new Line().Left("   ", Style.Plain).Flex("Video", Theme.Of(Theme.Dim));
            head.Right((sizes ? $"{"Original",10} {"AV1",10} " : string.Empty) + Saved("Saved"), Theme.Of(Theme.Dim));
            rows.Add(head.Build(width));
        }

        var items = new List<IRenderable>();
        if (ready.Count == 0) items.Add(new Line().Left("   Nothing is ready to finalise.", Theme.Of(Theme.Dim)).Build(width));
        foreach (var video in ready)
        {
            var selected = video.Id == m.ReviewSelection;
            if (selected) selectedRow = items.Count;
            Color? background = selected && focused ? Theme.Selection : null;
            var waiting = Waiting(m, video);
            var line = new Line()
                .Left(selected ? "▌" : " ", Theme.Of(focused ? Theme.Accent : Theme.Dim))
                .Left("  ", Style.Plain)
                .Flex(video.Name, Theme.Of(waiting ? Theme.Dim : Theme.Text, bold: selected));
            if (video.Warnings.Count > 0) line.Right(WarningMark(video) + "  ", Theme.Of(Theme.Held));
            if (sizes)
            {
                line.Right($"{(video.SizeBytes is { } original ? TuiText.Size(original) : "-"),10} ", Theme.Of(Theme.Dim));
                line.Right($"{(video.Av1Bytes is { } av1 ? TuiText.Size(av1) : "-"),10} ", Theme.Of(Theme.Soft));
            }
            if (FinalisingNow(m, video)) line.Right(Saved(FinalisingTag), Theme.Of(Theme.Held));
            else if (waiting) line.Right(Saved(QueuedTag), Theme.Of(Theme.Dim));
            else line.Right(Saved(video.PercentSaved is { } saved ? "-" + TuiText.Percent(saved) : "-"), Theme.Of(Theme.Ready, bold: true));
            items.Add(line.Build(width, background));
        }
        var start = Bars.WindowStart(items.Count, selectedRow, Math.Max(1, listCapacity));
        rows.AddRange(items.Skip(start).Take(Math.Max(1, listCapacity)));

        var pinned = warnings.Count + notes.Count + (showSummary ? 1 : 0);
        if (pinned > 0)
        {
            while (rows.Count < capacity - pinned) rows.Add(new Line().Build(width));
            rows.AddRange(warnings);
            rows.AddRange(notes);
        }
        if (showSummary)
        {
            // While stopping, the stop takes the saved total's place: the pane is too narrow for both.
            var summary = m.Finalising is { Counts: true } batch
                ? " " + string.Join(", ", new[]
                {
                    $"Finalising {batch.Position} of {batch.Ids.Count}",
                    batch.StopAsked ? null : $"{TuiText.Size(batch.SavedBytes)} saved so far",
                    batch.Failed > 0 ? $"{batch.Failed} failed" : null,
                    batch.StopAsked ? "stopping after this video" : null
                }.OfType<string>()) + "."
                : ready.Count == 0
                ? string.Empty
                : $" {ready.Count} ready to finalise, would save {TuiText.Size(ready.Sum(video => video.SavedBytes ?? 0))}.";
            rows.Add(new Line().Flex(summary, Theme.Of(Theme.Dim)).Build(width));
        }
        return rows;
    }

    /// <summary>The list rows the warnings leave, so the selected video and its neighbours stay in view as the cursor moves.</summary>
    private const int ListRowsKept = 3;

    /// <summary>
    /// The selected video's warnings under a dim heading, each wrapped under its text as the log wraps, since they are what to
    /// watch for before <c>f</c>. With less room than they need the first rows stay and the last one ends in an ellipsis; with
    /// no room for the heading and one row they give way entirely.
    /// </summary>
    private static List<IRenderable> WarningRows(Video? video, int width, int room) =>
        TextRows(video?.Warnings ?? [], "Validation warnings:", Theme.Held, width, room);

    /// <summary>
    /// The selected video's notes (VOPT-125) below its warnings, in the soft grey of ordinary text rather than the warning
    /// amber: a note asks nothing of the person, so it must not read as something to check.
    /// </summary>
    private static List<IRenderable> NoteRows(Video? video, int width, int room) =>
        TextRows(video?.JobNotes ?? [], "Notes:", Theme.Soft, width, room);

    private static List<IRenderable> TextRows(IReadOnlyList<string> texts, string heading, Color color, int width, int room)
    {
        if (texts.Count == 0 || room < 2) return [];
        const string indent = "     ";
        var parts = texts.SelectMany(text => TuiText.Wrap(text, Math.Max(1, width - indent.Length))).ToList();
        var shown = parts.Take(room - 1).ToList();
        if (shown.Count < parts.Count) shown[^1] = TuiText.Fit(shown[^1] + "…", Math.Max(1, width - indent.Length));
        return
        [
            new Line().Left("   ", Style.Plain).Flex(heading, Theme.Of(Theme.Dim)).Build(width),
            .. shown.Select(part => new Line().Left(indent, Style.Plain).Flex(part, Theme.Of(color)).Build(width))
        ];
    }
}
