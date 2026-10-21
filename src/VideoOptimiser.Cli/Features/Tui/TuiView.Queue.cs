using Spectre.Console;
using Spectre.Console.Rendering;

namespace VideoOptimiser.Cli.Features.Tui;

internal static partial class TuiView
{
    /// <summary>On a failed video, d dismisses rather than drops, so the footer names what it does there.</summary>
    private static bool FailedSelected(TuiModel m) => m.Failed.Any(video => video.Id == m.QueueSelection);

    private static FooterHint[] QueueHints(TuiModel m, FooterHint[] stop)
    {
        var keys = TuiKeys.Queue;
        var drop = FailedSelected(m) ? keys.Drop.HintAs(TuiKeys.QueueKeys.DismissFooter) : keys.Drop.Hint;
        return [Offer(m, keys.Add), keys.Select.Hint, keys.Hold.Hint, keys.Release.Hint, keys.RunNext.Hint, drop, keys.Retry.Hint, .. stop, .. Closing(m)];
    }

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
}
