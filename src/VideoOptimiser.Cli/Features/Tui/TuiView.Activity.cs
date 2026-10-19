using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli.Features.Tui;

internal static partial class TuiView
{
    /// <summary>End sits after start here, beside the scroll it undoes.</summary>
    private static (string Key, string What)[] ActivityHints((string Key, string What)[] stop)
    {
        var keys = TuiKeys.Activity;
        var anywhere = TuiKeys.Anywhere;
        return [keys.Scroll.Hint, keys.Fate.Hint, .. stop, anywhere.Start.Hint, keys.Follow.Hint, anywhere.NextPane.Hint, anywhere.Guide.Hint, anywhere.Quit.Hint];
    }

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
}
