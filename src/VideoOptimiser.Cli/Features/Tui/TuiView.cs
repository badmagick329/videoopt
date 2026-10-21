using Spectre.Console;
using Spectre.Console.Rendering;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// <c>View(model)</c>: a pure function from the model to Spectre renderables. Wide windows get two columns of two panes; narrow
/// ones stack the four panes and, when the height is short, collapse the unfocused ones to a single summary row.
/// </summary>
internal static partial class TuiView
{
    public const int WideFrom = 100;
    private const string Spinner = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    private static char SpinnerFrame(TuiModel m) => Spinner[(int)(m.Now.Ticks / 1_000_000 % Spinner.Length)];

    public static IRenderable Screen(TuiModel m)
    {
        if (m.Width < 40 || m.Height < 10)
            return new Paragraph($"Window too small: {m.Width}x{m.Height}. Needs 40x10.", Theme.Of(Theme.Held));

        var bodyHeight = m.Height - 2;
        // The cost page stands in for the panes; the header's counts stay, since a run goes on underneath.
        var body = m.Cost is { } page ? new Layout("body").Update(CostPageView(m, page, bodyHeight))
            : m.Width >= WideFrom ? Wide(m, bodyHeight) : Stacked(m, bodyHeight);
        return new Layout("root").SplitRows(
            new Layout("header").Size(1).Update(Header(m)),
            body,
            new Layout("footer").Size(1).Update(Footer(m)));
    }

    // ---- layout -----------------------------------------------------------------------------

    private static Layout Wide(TuiModel m, int height)
    {
        var leftWidth = m.Width * 55 / 100;
        var rightWidth = m.Width - leftWidth;
        var queueHeight = Math.Max(6, height * 58 / 100);
        var workerHeight = Math.Clamp(10, 6, Math.Max(6, height - 6));
        return new Layout("body").SplitColumns(
            new Layout("left").Size(leftWidth).SplitRows(
                new Layout("queue").Size(queueHeight).Update(PaneView(m, Pane.Queue, leftWidth, queueHeight)),
                new Layout("activity").Update(PaneView(m, Pane.Activity, leftWidth, height - queueHeight))),
            new Layout("right").SplitRows(
                new Layout("worker").Size(workerHeight).Update(PaneView(m, Pane.Worker, rightWidth, workerHeight)),
                new Layout("review").Update(PaneView(m, Pane.Review, rightWidth, height - workerHeight))));
    }

    private static Layout Stacked(TuiModel m, int height)
    {
        Pane[] panes = [Pane.Queue, Pane.Worker, Pane.Activity, Pane.Review];
        var heights = StackHeights(height, m.Focus);
        var rows = new List<Layout>();
        for (var i = 0; i < panes.Length; i++)
        {
            if (heights[i] == 0) continue;
            var leaf = new Layout(panes[i].ToString()).Update(PaneView(m, panes[i], m.Width, heights[i]));
            rows.Add(i == panes.Length - 1 || heights.Skip(i + 1).All(h => h == 0) ? leaf : leaf.Size(heights[i]));
        }
        return new Layout("body").SplitRows([.. rows]);
    }

    /// <summary>Row counts for Queue, Worker, Activity, Review (0 = left out). Short windows collapse the unfocused panes.</summary>
    private static int[] StackHeights(int height, Pane focus)
    {
        const int collapsed = 3;
        if (height >= 28)
        {
            int[] weights = [4, 3, 4, 3];
            var heights = weights.Select(w => height * w / 14).ToArray();
            heights[0] += height - heights.Sum();
            return heights;
        }
        var focusedHeight = height - 3 * collapsed;
        if (focusedHeight < 5)
        {
            var only = new int[4];
            only[(int)focus] = height;
            return only;
        }
        var result = new[] { collapsed, collapsed, collapsed, collapsed };
        result[(int)focus] = focusedHeight;
        return result;
    }

    // ---- header and footer ------------------------------------------------------------------

    private static IRenderable Header(TuiModel m)
    {
        var line = new Line().Left(" VideoOptimiser", Theme.Of(Theme.Text, bold: true));

        // The ready chip stays in the narrow header, so the videos F leaves out are counted wherever the count of ready ones is.
        var toCheck = m.Ready().Count(video => video.Warnings.Count > 0);
        var chips = new List<(string Label, Color Color)>
        {
            ($"{m.Count(VideoGroup.ConvertingNow)} converting", Theme.Converting),
            ($"{m.Count(VideoGroup.Queue)} in the queue", Theme.Queue),
            ($"{m.Count(VideoGroup.Held)} held", Theme.Held),
            ($"{m.Count(VideoGroup.Ready)} ready{(toCheck > 0 ? $", {toCheck} to check" : string.Empty)}", Theme.Ready)
        };
        if (m.Count(VideoGroup.Failed) > 0) chips.Add(($"{m.Count(VideoGroup.Failed)} failed", Theme.Failed));

        var subtitle = "  " + m.Subtitle;
        var room = m.Width - " VideoOptimiser".Length - 1;
        if (m.Subtitle.Length > 0 && room >= TuiText.Cells(subtitle) + chips.Sum(chip => 1 + 1 + TuiText.Cells(chip.Label) + 2)) line.Left(subtitle, Theme.Of(Theme.Dim));
        else if (chips.Count > 4) chips = [chips[0], chips[3], chips[4]];
        else chips = [chips[0], chips[3]];

        foreach (var (label, color) in chips)
        {
            line.Right("● ", Theme.Of(color));
            line.Right(label + "  ", Theme.Of(Theme.Soft));
        }
        return line.Build(m.Width);
    }

    /// <summary>The footer's hints in order, each marked when a held busy key blocks it; the footer draws as many as fit.</summary>
    internal static FooterHint[] FooterHints(TuiModel m)
    {
        var anywhere = TuiKeys.Anywhere;
        // The stop keys work from every pane, so every footer offers them while a run is going.
        // x toggles: with a stop after this video asked, its word is the way back.
        FooterHint[] stop = m.Run is null ? []
            : [Offer(m, anywhere.StopAfter, m.Requests is { StopAfterThisVideo: true, StopNow: false } ? "carry on" : null), Offer(m, anywhere.StopNow)];
        // Esc works from every pane, but Review is where the batch shows, so its footer offers it.
        FooterHint[] stopFinalise = m.Finalising is { CanStop: true } ? [TuiKeys.Review.StopFinalise.Hint] : [];
        FooterHint[] hints = m.Help
            ? [new("any key", "close the help")]
            : m.Dialog is TuiConfirm
            ? [new("y", "confirm"), new("any other key", "cancel")]
            : m.Dialog is TuiChoice
                ? [new("↑↓", "move"), new("enter", "choose"), new("esc", "cancel")]
                : m.Dialog is TuiTextInput
                ? [new("enter", "add"), new("esc", "cancel"), new("backspace", "edit")]
                : m.Cost is { } page
                ? [.. page.Report is { Runs.Count: > 0 } ? [new("↑↓", "select a run"), new("pgup pgdn", "scroll its videos")] : Array.Empty<FooterHint>(),
                    new(TuiKeys.Anywhere.Cost.Name + " / esc", "close"), new("ctrl+c", "quit")]
                : m.Focus switch
                {
                    Pane.Queue => QueueHints(m, stop),
                    Pane.Worker => WorkerHints(m, stop),
                    Pane.Activity => ActivityHints(m, stop),
                    _ => ReviewHints(m, stop, stopFinalise)
                };

        if (Adding(m)) hints = [new("esc", "cancel add"), .. hints.Where(hint => hint.Key != "esc")
            .Select(hint => hint.Key == anywhere.Cost.Name + " / esc" ? hint with { Key = anywhere.Cost.Name } : hint)];
        return hints;
    }

    /// <summary>
    /// A key's hint, greyed while <see cref="TuiUpdate.BlockedBy"/> says pressing it would be refused: the same check the key's
    /// handler refuses through, so the footer and the key never disagree.
    /// </summary>
    private static FooterHint Offer(TuiModel m, TuiKey key, string? what = null) =>
        (what is null ? key.Hint : key.HintAs(what)) with { Blocked = TuiUpdate.BlockedBy(m, key) is not null };

    private static bool Adding(TuiModel m) => m.Dialog is null && !m.Help && m.Busy.ContainsKey(AddVideos.Key);

    private static IRenderable Footer(TuiModel m)
    {
        var hints = FooterHints(m);
        var adding = Adding(m);

        // A notice says why a key did nothing; a read problem stays until the next read works.
        var notice = m.Notice?.Text ?? m.Busy.Values.FirstOrDefault() ?? m.ReadProblem;
        var noticeText = notice is null ? string.Empty : TuiText.Fit(notice, adding ? Math.Max(0, m.Width - 32) : Math.Max(10, m.Width * 2 / 3)) + " ";
        var line = new Line();
        var used = 1;
        if (m.Dialog is null)
        {
            var name = (m.Cost is null ? m.Focus.ToString() : "Cost") + "  ";
            line.Left(" " + name, Theme.Of(Theme.Accent, bold: true));
            used += name.Length;
        }
        else line.Left(" ", Style.Plain);

        var budget = m.Width - TuiText.Cells(noticeText) - (noticeText.Length > 0 ? 2 : 0);
        // A full Queue footer can fit its keys with two spaces when dotted separators would lose the last ones.
        var hintCells = hints.Sum(hint => TuiText.Cells(hint.Key) + (hint.What.Length > 0 ? 1 + TuiText.Cells(hint.What) : 0));
        var separator = used + hintCells + Math.Max(0, hints.Length - 1) * 3 > budget ? "  " : " · ";
        var first = true;
        foreach (var (key, what, blocked) in hints)
        {
            var cost = TuiText.Cells(key) + (what.Length > 0 ? 1 + what.Length : 0) + (first ? 0 : separator.Length);
            if (used + cost > budget) break;
            if (!first) line.Left(separator, Theme.Of(Theme.Faint));
            // A blocked key keeps its place, drawn in the dimmest colour, so it reads as there but unavailable for now.
            line.Left(key, blocked ? Theme.Of(Theme.Faint) : Theme.Of(Theme.Text, bold: true));
            if (what.Length > 0) line.Left(" " + what, Theme.Of(blocked ? Theme.Faint : Theme.Dim));
            used += cost;
            first = false;
        }
        if (noticeText.Length > 0) line.Right(noticeText, Theme.Of(Theme.Held));
        return line.Build(m.Width);
    }

    // Each pane's hints: its own keys first, then the ones that work from every pane, in the order the footer has room for.

    private static FooterHint[] Closing(TuiModel m) =>
        [Offer(m, TuiKeys.Anywhere.Start), TuiKeys.Anywhere.NextPane.Hint, TuiKeys.Anywhere.Guide.Hint, TuiKeys.Anywhere.Quit.Hint];

    // ---- panes ------------------------------------------------------------------------------

    private static Panel PaneView(TuiModel m, Pane pane, int width, int height)
    {
        var focused = m.Focus == pane;
        var inner = width - 4;
        var collapsed = height <= 3;
        List<IRenderable> rows = collapsed
            ? [CollapsedRow(m, pane, inner)]
            : pane switch
            {
                Pane.Queue => QueueRows(m, inner, height - 2, focused),
                Pane.Worker => WorkerRows(m, inner, height - 2),
                Pane.Activity => ActivityRows(m, inner, height - 2),
                _ => ReviewRows(m, inner, height - 2, focused)
            };
        var sub = collapsed ? string.Empty : pane switch
        {
            Pane.Queue => QueueSub(m),
            Pane.Worker => WorkerView.Of(m.Worker).Info?.Status == RemoteWorkerStatuses.Bootstrapping ? "bootstrapping" : string.Empty,
            Pane.Activity => ActivitySub(m),
            _ => m.Finalising is { Counts: true } batch ? $"finalising {batch.Position} of {batch.Ids.Count}"
                : m.Ready() is { Count: > 0 } ready ? $"{ready.Count} ready" : string.Empty
        };
        return Frame(pane.ToString(), sub, focused, height, rows);
    }

    private static Panel Frame(string title, string sub, bool focused, int height, IReadOnlyList<IRenderable> rows)
    {
        var titleColor = focused ? Theme.Accent : Theme.Soft;
        var header = $"[bold {titleColor.ToMarkup()}] {Markup.Escape(title)} [/]"
            + (sub.Length > 0 ? $"[{Theme.Dim.ToMarkup()}]{Markup.Escape(sub)} [/]" : string.Empty);
        return new Panel(new Rows(rows.Take(Math.Max(1, height - 2))))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Theme.Of(focused ? Theme.Accent : Theme.Faint),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true,
            Height = height,
            Header = new PanelHeader(header)
        };
    }

    private static IRenderable CollapsedRow(TuiModel m, Pane pane, int width)
    {
        var line = new Line();
        switch (pane)
        {
            case Pane.Queue:
                line.Flex($"{m.Count(VideoGroup.ConvertingNow)} converting, {m.Count(VideoGroup.Queue)} in the queue, {m.Count(VideoGroup.Held)} held", Theme.Of(Theme.Soft));
                break;
            case Pane.Worker:
                var view = WorkerView.Of(m.Worker);
                line.Flex(!m.Worker.Managed ? "No managed worker" : view.Info is null ? "No worker" : $"{view.Info.Name}, {RemoteWorkerStatuses.Describe(view.Info.Status)}", Theme.Of(Theme.Soft));
                if (view.Cost(m.Now) is { } cost) line.Right(TuiText.Eur(cost), Theme.Of(Theme.Text));
                break;
            case Pane.Activity:
                line.Flex(m.Converting is { } video && StageOf(video.Status) is { } stage ? $"{StageName(stage)} {video.Name}" : IdleReason(m), Theme.Of(Theme.Soft));
                break;
            default:
                var ready = m.Ready();
                line.Flex($"{ready.Count} ready to finalise", Theme.Of(Theme.Soft));
                line.Right(TuiText.Size(ready.Sum(video => video.SavedBytes ?? 0)) + " to save", Theme.Of(Theme.Ready));
                break;
        }
        return line.Build(width);
    }

    private const string FinalisingTag = "finalising";
    private const string QueuedTag = "queued";

    /// <summary>A ready video a finalise batch has yet to reach: both panes dim it and tag it <c>queued</c>.</summary>
    private static bool Waiting(TuiModel m, Video video) => video.Group == VideoGroup.Ready && m.Finalising?.IsWaiting(video.Id) == true;

    /// <summary>A ready video a finalise batch is on now. The batch says so before the snapshot's status catches up.</summary>
    private static bool FinalisingNow(TuiModel m, Video video) => video.Group == VideoGroup.Ready && m.Finalising?.Current == video.Id;

    public static Stage? StageOf(JobStatus status) => status switch
    {
        JobStatus.Staging => Stage.Uploading,
        JobStatus.CrfSearching => Stage.FindingCrf,
        JobStatus.Encoding => Stage.Encoding,
        JobStatus.Downloading => Stage.Downloading,
        JobStatus.Validating => Stage.Checking,
        _ => null
    };

    public static string StageName(Stage stage) => stage switch
    {
        Stage.Uploading => "Uploading",
        Stage.FindingCrf => "Finding CRF",
        Stage.Encoding => "Encoding",
        Stage.Downloading => "Downloading",
        _ => "Checking"
    };

    private static string WarningMark(Video video) => video.Warnings.Count == 1 ? "1 warning" : $"{video.Warnings.Count} warnings";
}
