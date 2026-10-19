using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
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

    /// <summary>On a failed video, d dismisses rather than drops, so the footer names what it does there.</summary>
    private static bool FailedSelected(TuiModel m) => m.Failed.Any(video => video.Id == m.QueueSelection);

    private static IRenderable Footer(TuiModel m)
    {
        var anywhere = TuiKeys.Anywhere;
        // The stop keys work from every pane, so every footer offers them while a run is going.
        (string Key, string What)[] stop = m.Run is null ? [] : [anywhere.StopAfter.Hint, anywhere.StopNow.Hint];
        // Esc works from every pane, but Review is where the batch shows, so its footer offers it.
        (string Key, string What)[] stopFinalise = m.Finalising is { CanStop: true } ? [TuiKeys.Review.StopFinalise.Hint] : [];
        (string Key, string What)[] hints = m.Help
            ? [("any key", "close the help")]
            : m.Dialog is TuiConfirm
            ? [("y", "confirm"), ("any other key", "cancel")]
            : m.Dialog is TuiChoice
                ? [("↑↓", "move"), ("enter", "choose"), ("esc", "cancel")]
                : m.Cost is { } page
                ? [.. page.Report is { Runs.Count: > 0 } ? [("↑↓", "select a run"), ("pgup pgdn", "scroll its videos")] : Array.Empty<(string, string)>(),
                    (TuiKeys.Anywhere.Cost.Name + " / esc", "close"), ("ctrl+c", "quit")]
                : m.Focus switch
                {
                    Pane.Queue => QueueHints(m, stop),
                    Pane.Worker => WorkerHints(stop),
                    Pane.Activity => ActivityHints(stop),
                    _ => ReviewHints(stop, stopFinalise)
                };

        // A notice says why a key did nothing; a read problem stays until the next read works.
        var notice = m.Notice?.Text ?? m.Busy.Values.FirstOrDefault() ?? m.ReadProblem;
        var noticeText = notice is null ? string.Empty : TuiText.Fit(notice, Math.Max(10, m.Width * 2 / 3)) + " ";
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
        var first = true;
        foreach (var (key, what) in hints)
        {
            var cost = TuiText.Cells(key) + (what.Length > 0 ? 1 + what.Length : 0) + (first ? 0 : 3);
            if (used + cost > budget) break;
            if (!first) line.Left(" · ", Theme.Of(Theme.Faint));
            line.Left(key, Theme.Of(Theme.Text, bold: true));
            if (what.Length > 0) line.Left(" " + what, Theme.Of(Theme.Dim));
            used += cost;
            first = false;
        }
        if (noticeText.Length > 0) line.Right(noticeText, Theme.Of(Theme.Held));
        return line.Build(m.Width);
    }

    // Each pane's hints: its own keys first, then the ones that work from every pane, in the order the footer has room for.

    private static (string Key, string What)[] Closing() =>
        [TuiKeys.Anywhere.Start.Hint, TuiKeys.Anywhere.NextPane.Hint, TuiKeys.Anywhere.Guide.Hint, TuiKeys.Anywhere.Quit.Hint];

    private static (string Key, string What)[] QueueHints(TuiModel m, (string Key, string What)[] stop)
    {
        var keys = TuiKeys.Queue;
        var drop = FailedSelected(m) ? keys.Drop.HintAs(TuiKeys.QueueKeys.DismissFooter) : keys.Drop.Hint;
        return [keys.Select.Hint, keys.Hold.Hint, keys.Release.Hint, keys.RunNext.Hint, drop, keys.Retry.Hint, .. stop, .. Closing()];
    }

    private static (string Key, string What)[] WorkerHints((string Key, string What)[] stop)
    {
        var keys = TuiKeys.Worker;
        return [keys.Create.Hint, keys.Delete.Hint, keys.Check.Hint, .. stop, .. Closing()];
    }

    /// <summary>End sits after start here, beside the scroll it undoes.</summary>
    private static (string Key, string What)[] ActivityHints((string Key, string What)[] stop)
    {
        var keys = TuiKeys.Activity;
        var anywhere = TuiKeys.Anywhere;
        return [keys.Scroll.Hint, keys.Fate.Hint, .. stop, anywhere.Start.Hint, keys.Follow.Hint, anywhere.NextPane.Hint, anywhere.Guide.Hint, anywhere.Quit.Hint];
    }

    private static (string Key, string What)[] ReviewHints((string Key, string What)[] stop, (string Key, string What)[] stopFinalise)
    {
        var keys = TuiKeys.Review;
        return [keys.Select.Hint, keys.OpenOriginal.Hint, keys.OpenAv1.Hint, keys.Finalise.Hint, keys.FinaliseAll.Hint, keys.Reject.Hint, .. stopFinalise, .. stop, .. Closing()];
    }

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

    // ---- queue ------------------------------------------------------------------------------

    private static readonly (string Header, Color Color)[] GroupHeads =
    [
        ("Converting now", Theme.Converting),
        ("Queue, in run order", Theme.Queue),
        ("Held", Theme.Held),
        ("Ready to finalise", Theme.Ready),
        ("Failed, latest first", Theme.Failed)
    ];

    private static string QueueSub(TuiModel m)
    {
        var ordered = m.Ordered();
        var at = ordered.FindIndex(video => video.Id == m.QueueSelection);
        return ordered.Count == 0 ? "empty" : $"{at + 1}/{ordered.Count}";
    }

    private static List<IRenderable> QueueRows(TuiModel m, int width, int capacity, bool focused)
    {
        var ordered = m.Ordered();
        if (!m.Loaded) return [new Line().Flex("Reading the queue.", Theme.Of(Theme.Dim)).Build(width)];
        if (ordered.Count == 0) return [new Line().Flex("The queue is empty.", Theme.Of(Theme.Dim)).Build(width)];

        var all = new List<IRenderable>();
        var selectedRow = 0;
        VideoGroup? lastGroup = null;
        var position = 0;
        foreach (var video in ordered)
        {
            if (video.Group != lastGroup)
            {
                var (head, color) = GroupHeads[(int)video.Group];
                all.Add(new Line().Left("  " + head, Theme.Of(color, bold: true)).Left($"  {m.Count(video.Group)}", Theme.Of(Theme.Dim)).Build(width));
                lastGroup = video.Group;
                position = 0;
            }
            if (video.Group == VideoGroup.Queue) position++;

            var selected = video.Id == m.QueueSelection;
            if (selected) selectedRow = all.Count;
            Color? background = selected && focused ? Theme.Selection : null;

            var line = new Line()
                .Left(selected ? "▌" : " ", Theme.Of(focused ? Theme.Accent : Theme.Dim))
                .Left(video.Group == VideoGroup.Queue ? $" {position,2}  " : "    ", Theme.Of(Theme.Dim))
                .Flex(video.Name, Theme.Of(Waiting(m, video) ? Theme.Dim : video.Group is VideoGroup.Held or VideoGroup.Failed ? Theme.Soft : Theme.Text, bold: selected));
            QueueRight(line, video, m);
            all.Add(line.Build(width, background));
        }

        var start = Bars.WindowStart(all.Count, selectedRow, capacity);
        return [.. all.Skip(start).Take(capacity)];
    }

    private const string FinalisingTag = "finalising";
    private const string QueuedTag = "queued";

    /// <summary>A ready video a finalise batch has yet to reach: both panes dim it and tag it <c>queued</c>.</summary>
    private static bool Waiting(TuiModel m, Video video) => video.Group == VideoGroup.Ready && m.Finalising?.IsWaiting(video.Id) == true;

    /// <summary>A ready video a finalise batch is on now. The batch says so before the snapshot's status catches up.</summary>
    private static bool FinalisingNow(TuiModel m, Video video) => video.Group == VideoGroup.Ready && m.Finalising?.Current == video.Id;

    private static void QueueRight(Line line, Video video, TuiModel m)
    {
        var size = video.SizeBytes is { } bytes ? TuiText.Size(bytes) : string.Empty;
        switch (video)
        {
            // The batch's own tags stand over the snapshot's note, which says finalising only once the job's status says so.
            case { Group: VideoGroup.Ready } when FinalisingNow(m, video):
                line.Right(FinalisingTag + " ", Theme.Of(Theme.Held));
                break;
            case { Group: VideoGroup.Ready } when Waiting(m, video):
                line.Right(QueuedTag + " ", Theme.Of(Theme.Dim));
                break;
            case { Group: VideoGroup.ConvertingNow } when StageOf(video.Status) is { } stage:
                line.Right($"{StageName(stage)} ", Theme.Of(Theme.Converting));
                break;
            case { Stopped: true }:
                line.Right("stopped", Theme.Of(Theme.Stopped));
                line.Right($" · {size} ", Theme.Of(Theme.Dim));
                break;
            case { Group: VideoGroup.Ready }:
                if (video.Note is not null)
                {
                    line.Right(video.Note + " ", Theme.Of(Theme.Held));
                    break;
                }
                if (video.Warnings.Count > 0) line.Right(WarningMark(video), Theme.Of(Theme.Held)).Right(" · ", Theme.Of(Theme.Dim));
                if (video.PercentSaved is { } saved) line.Right($"saves {TuiText.Percent(saved)} ", Theme.Of(Theme.Ready));
                else line.Right(size + " ", Theme.Of(Theme.Dim));
                break;
            case { Group: VideoGroup.Failed }:
                line.Right((video.Note ?? "failed") + " ", Theme.Of(Theme.Failed));
                break;
            default:
                line.Right(size + " ", Theme.Of(Theme.Dim));
                break;
        }
    }

    // ---- worker -----------------------------------------------------------------------------

    /// <summary>The pane, with what an action on the worker is doing now ("Creating the worker.") on its last row.</summary>
    private static List<IRenderable> WorkerRows(TuiModel m, int width, int capacity)
    {
        var rows = WorkerRowsCore(m, width, capacity);
        if (!m.Busy.TryGetValue(BusyKeys.Worker, out var working)) return rows;
        var busy = new Line().Left(" ", Style.Plain).Flex(working, Theme.Of(Theme.Held)).Build(width);
        if (rows.Count >= capacity) rows[^1] = busy;
        else rows.Add(busy);
        return rows;
    }

    private static List<IRenderable> WorkerRowsCore(TuiModel m, int width, int capacity)
    {
        var rows = new List<IRenderable>();
        IRenderable Pair(string label, string value, Style? valueStyle = null) =>
            new Line().Left($" {label,-13}", Theme.Of(Theme.Dim)).Flex(value, valueStyle ?? Theme.Of(Theme.Text)).Build(width);

        if (m.Worker.Lifecycle.Length == 0)
            return [new Line().Left(" ○ ", Theme.Of(Theme.Dim)).Flex("Reading the worker.", Theme.Of(Theme.Dim)).Build(width)];

        // One line, with the reason: the lifecycle is the only thing that makes a worker managed.
        if (!m.Worker.Managed)
            return [new Line().Left(" ○ ", Theme.Of(Theme.Dim)).Flex($"No managed worker: remoteSsh.lifecycle is \"{m.Worker.Lifecycle}\".", Theme.Of(Theme.Soft)).Build(width)];

        var view = WorkerView.Of(m.Worker);
        if (view.Info is not { } worker)
        {
            rows.Add(new Line().Left(" ○ ", Theme.Of(Theme.Dim)).Flex("No worker", Theme.Of(Theme.Soft, bold: true)).Build(width));
            rows.Add(new Line().Flex(view.NotCheckedReason is { } why
                ? $"   couldn't check: {why}"
                : view.Asking ? "   Asking Hetzner." : "   Nothing is billing.", Theme.Of(view.NotCheckedReason is null ? Theme.Dim : Theme.Held)).Build(width));
            return [.. rows.Take(capacity)];
        }

        var status = worker.Status;
        var settling = status is RemoteWorkerStatuses.Bootstrapping or "initializing" or "starting";
        var statusColor = status == "running" ? Theme.Ready : settling ? Theme.Held : status == RemoteWorkerStatuses.Recorded ? Theme.Soft : Theme.Failed;
        rows.Add(new Line()
            .Left(settling ? $" {SpinnerFrame(m)} " : " ● ", Theme.Of(statusColor))
            .Flex(worker.Name, Theme.Of(Theme.Text, bold: true))
            .Right(RemoteWorkerStatuses.Describe(status) + " ", Theme.Of(statusColor))
            .Build(width));
        var place = string.Join(" · ", new[] { worker.ServerType, worker.Location }.Where(part => part.Length > 0));
        rows.Add(new Line().Flex(place.Length > 0 ? "   " + place : string.Empty, Theme.Of(Theme.Dim)).Build(width));
        rows.Add(new Line().Build(width));
        if (view.Uptime(m.Now) is { } uptime) rows.Add(Pair("Uptime", HumanReadableValues.FormatDuration(uptime)));
        rows.Add(view.Price is { } price
            ? Pair("Price", $"{HourlyPrice.Format(price)} incl. VAT")
            : Pair("Price", view.Asking ? "asking Hetzner" : "unknown", Theme.Of(Theme.Dim)));
        if (view.Cost(m.Now) is { } cost) rows.Add(Pair("Cost so far", TuiText.Eur(cost), Theme.Of(Theme.Text, bold: true)));
        if (view.NotCheckedReason is { } reason) rows.Add(Pair("Hetzner", $"couldn't check: {reason}", Theme.Of(Theme.Held)));
        return [.. rows.Take(capacity)];
    }

    // ---- activity ---------------------------------------------------------------------------

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

    /// <summary>Why nothing converts, from the run and its events.</summary>
    public static string IdleReason(TuiModel m)
    {
        if (m.Run is null) return "No run is going.";
        if (m.Activity is { Phase: RunPhase.Preparing, WaitingForWorker: true }) return "The run is waiting for the worker.";
        return m.Activity.Phase switch
        {
            RunPhase.Preparing => "The run is getting ready.",
            RunPhase.Ending => "The run is ending.",
            _ => m.Count(VideoGroup.Queue) > 0 ? "The run is between videos." : "The queue is empty."
        };
    }

    private static List<IRenderable> ActivityRows(TuiModel m, int width, int capacity)
    {
        var rows = new List<IRenderable>();
        var video = m.Converting;

        if (video is not null && StageOf(video.Status) is { } stage)
        {
            var workerName = WorkerView.Of(m.Worker).Info?.Name;
            rows.Add(new Line()
                .Left(StageName(stage), Theme.Of(Theme.Converting, bold: true))
                .Left("  ", Style.Plain)
                .Flex(video.Name, Theme.Of(Theme.Text))
                .Right(video.Remote ? (workerName is null ? "on the worker" : $"on {workerName}") : "on this PC", Theme.Of(Theme.Dim))
                .Build(width));

            rows.Add(ProgressLine(m, stage, width).Build(width));

            rows.Add(Stepper(video, stage, width));
        }
        else
        {
            rows.Add(new Line().Left("Idle", Theme.Of(Theme.Soft, bold: true)).Left("  " + IdleReason(m), Theme.Of(Theme.Dim)).Build(width));
            rows.Add(new Line().Build(width));
            rows.Add(new Line().Build(width));
        }
        rows.Add(RunLine(m).Build(width));

        var rule = new Paragraph();
        rule.Append("── Log ", Theme.Of(Theme.Faint));
        rule.Append(new string('─', Math.Max(0, width - 7)), Theme.Of(Theme.Faint));
        rows.Add(rule);

        rows.AddRange(LogRows(m, width, Math.Max(0, capacity - rows.Count)));
        return rows;
    }

    /// <summary>How long a converting video may go without any event before its progress line says so.</summary>
    internal static readonly TimeSpan QuietAfter = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The converting video's latest progress line. A reading (an encode, a transfer) gets a bar for its own step only: ab-av1
    /// gives no fraction across stages; its text carries the time left. Before a step's first line a spinner stands in, after
    /// the empty bar of a stage whose lines are readings, so the bar does not move when they arrive. When the run has said
    /// nothing for <see cref="QuietAfter"/>, the line says so: for a remote stage, when the worker last answered, since a CRF
    /// test clip can take minutes on a CPU worker and only the answers tell that from a lost worker. Recent answers are dim (the
    /// text already says what is running, and the line has no room for more); no answers, or none for a minute, is a warning
    /// with how long. A lost worker also logs every resend.
    /// </summary>
    private static Line ProgressLine(TuiModel m, Stage stage, int width)
    {
        var activity = m.Activity;
        var line = new Line();
        var cells = Math.Clamp(width / 4, 10, 30);
        if (activity.ProgressPercent is { } percent && width >= 48)
        {
            var filled = (int)Math.Round(cells * percent / 100d);
            line.Left(new string('━', filled), Theme.Of(Theme.Converting))
                .Left(new string('─', cells - filled), Theme.Of(Theme.Faint))
                .Left("  ", Style.Plain);
        }
        if (activity.ProgressText is { } text)
        {
            line.Flex(text, Theme.Of(Theme.Soft));
        }
        else
        {
            if (stage is Stage.Uploading or Stage.Encoding or Stage.Downloading && width >= 48)
                line.Left(new string('─', cells), Theme.Of(Theme.Faint)).Left("  ", Style.Plain);
            line.Flex(SpinnerFrame(m).ToString(), Theme.Of(Theme.Converting));
        }
        if (activity.LastEventAt is { } at && m.Now - at >= QuietAfter)
        {
            var quiet = HumanReadableValues.FormatDuration(m.Now - at);
            if (activity.LastAnswerAt is not { } answered || answered < at)
                line.Right($"no update for {quiet}", Theme.Of(Theme.Held));
            else if (m.Now - answered < QuietAfter)
                line.Right($"worker answered {HumanReadableValues.FormatDuration(m.Now - answered)} ago", Theme.Of(Theme.Dim));
            else
                line.Right($"no answer from the worker for {HumanReadableValues.FormatDuration(m.Now - answered)}", Theme.Of(Theme.Held));
        }
        return line;
    }

    private const int LogStampCells = 10;
    private const string DetailIndent = "  ";

    /// <summary>
    /// The newest entries that fit, each wrapped under its text so a long line is read whole. Scrolling still moves by entry.
    /// An entry taller than the whole space keeps its first rows.
    /// </summary>
    private static List<IRenderable> LogRows(TuiModel m, int width, int capacity)
    {
        var shown = new List<List<IRenderable>>();
        var used = 0;
        for (var i = m.Log.Count - m.LogScroll - 1; i >= 0 && used < capacity; i--)
        {
            var entry = m.Log[i];
            var style = Theme.Of(entry.Kind switch
            {
                LogKind.Stage => Theme.Converting,
                LogKind.Success => Theme.Ready,
                LogKind.Warning => Theme.Held,
                LogKind.Error => Theme.Failed,
                LogKind.Action => Theme.Text,
                _ => Theme.Dim
            });
            var indent = entry.Kind == LogKind.Detail ? DetailIndent : string.Empty;
            var text = TuiText.Wrap(entry.Text, Math.Max(1, width - LogStampCells - indent.Length)).Select(part => indent + part).ToList();
            if (used + text.Count > capacity && shown.Count > 0) break;
            var stamp = entry.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  ";
            var entryRows = text.Take(capacity - used)
                .Select((part, at) => new Line().Left(at == 0 ? stamp : new string(' ', LogStampCells), Theme.Of(Theme.Dim)).Flex(part, style).Build(width))
                .ToList();
            shown.Add(entryRows);
            used += entryRows.Count;
        }
        shown.Reverse();
        return [.. shown.SelectMany(entryRows => entryRows)];
    }

    /// <summary>
    /// What a person needs before stopping the run from another window: its kind, when it started and its process. A foreground run
    /// is named by its command, since only Ctrl+C in its own window stops it.
    /// </summary>
    private static Line RunLine(TuiModel m)
    {
        if (m.Run is not { } run) return new Line();
        var kind = run.IsBackground ? "Background run" : $"Foreground run ({run.Command.Replace("video-optimiser ", string.Empty, StringComparison.Ordinal)})";
        return new Line().Flex($"{kind}, started {CliLocks.FormatStarted(run.StartedUtc)} (process {run.ProcessId.ToString(CultureInfo.InvariantCulture)})", Theme.Of(Theme.Dim));
    }

    /// <summary>
    /// The pane title's note: a stop asked of the run says so, since it changes what happens next; otherwise a background
    /// run's worker fate, which only a background run has (a foreground run asks in its own window).
    /// </summary>
    private static string ActivitySub(TuiModel m)
    {
        var scroll = m.LogScroll > 0 ? $"{m.LogScroll} lines up" : "following";
        if (m.Run is null) return scroll;
        if (m.Requests is { StopNow: true }) return scroll + " · stopping now";
        if (m.Requests is { StopAfterThisVideo: true }) return scroll + " · stopping after this video";
        if (m.Run.IsBackground && m.Worker.Managed && TuiUpdate.FateOf(m) is { } fate)
            return scroll + (fate == WorkerFateChoice.Delete ? " · worker deleted at the end" : " · worker kept at the end");
        return scroll;
    }

    /// <summary>Only the stages this video passes through: a video converting on this PC has no upload or download.</summary>
    private static Paragraph Stepper(Video video, Stage current, int width)
    {
        Stage[] stages = video.Remote
            ? [Stage.Uploading, Stage.FindingCrf, Stage.Encoding, Stage.Downloading, Stage.Checking]
            : [Stage.FindingCrf, Stage.Encoding, Stage.Checking];
        var parts = stages.Select(s =>
        {
            var symbol = s < current ? "✓" : s == current ? "●" : "○";
            var color = s < current ? Theme.Ready : s == current ? Theme.Converting : Theme.Faint;
            return (Symbol: symbol, Name: StageName(s), Color: color, Current: s == current, Done: s < current);
        }).ToList();

        var full = parts.Sum(p => p.Symbol.Length + 1 + p.Name.Length) + 2 * (parts.Count - 1);
        var paragraph = new Paragraph();
        if (full > width)
        {
            paragraph.Append($"Stage {Math.Max(0, Array.IndexOf(stages, current)) + 1} of {parts.Count}", Theme.Of(Theme.Dim));
            return paragraph;
        }
        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            if (i > 0) paragraph.Append("  ", Style.Plain);
            paragraph.Append(p.Symbol + " ", Theme.Of(p.Color));
            paragraph.Append(p.Name, Theme.Of(p.Current ? Theme.Text : p.Done ? Theme.Soft : Theme.Dim, bold: p.Current));
        }
        return paragraph;
    }

    // ---- review -----------------------------------------------------------------------------

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

    private static string WarningMark(Video video) => video.Warnings.Count == 1 ? "1 warning" : $"{video.Warnings.Count} warnings";

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

    // ---- dialog -----------------------------------------------------------------------------

    public static IRenderable? Dialog(TuiModel m)
    {
        if (m.Help) return HelpDialog(m);
        return m.Dialog switch
        {
            TuiChoice choice => ChoiceDialog(m, choice),
            TuiConfirm confirm => ConfirmDialog(m, confirm),
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

    private static Panel DialogPanel(string title, int width, List<IRenderable> rows) => new(new Rows(rows))
    {
        Border = BoxBorder.Rounded,
        BorderStyle = Theme.Of(Theme.Accent),
        Padding = new Padding(2, 0, 2, 0),
        Width = width,
        Header = new PanelHeader($"[bold {Theme.Accent.ToMarkup()}] {Markup.Escape(title)} [/]")
    };

    /// <summary>
    /// Every key, grouped as the footers group them, in the dialogs' style. A window too short for every row shows what fits:
    /// the per-pane groups drop from the end, and the Anywhere group alone is cut at the window's last row.
    /// </summary>
    private static Panel HelpDialog(TuiModel m)
    {
        // Wide enough for the longest key row, the stop keys' shared one, to read whole in a wide window.
        var width = Math.Clamp(m.Width - 6, 30, 94);
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
            rows.Add(new Paragraph(" "));
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
