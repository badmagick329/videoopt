using Spectre.Console;
using Spectre.Console.Rendering;
using VideoOptimiser.Application.Features.Cost;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Cli.Features.Cost;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// The cost page, drawn in place of the four panes: the all-time figure, the runs newest first, the selected run's videos, and
/// a dim line on why the three levels don't add up. The figures and the all-time sentences are <c>cost</c>'s own
/// (<see cref="CostReportService"/>, <see cref="CostPrice"/>, <see cref="CostCommand"/>), so the page and the command agree.
/// Wording goes through the humanizer gate (docs index).
/// </summary>
internal static partial class TuiView
{
    internal const string CostEmpty = "Nothing to show yet: this page fills in after a run on a Hetzner worker.";

    internal const string CostFootnote = "Per-video, per-run and all-time figures don't add up: all time is what Hetzner billed, in started hours, idle time included.";

    private const string Gap = "  ";

    private static Panel CostPageView(TuiModel m, CostPage page, int height) =>
        Frame("Cost", "EUR per TB freed on Hetzner workers", focused: true, height, CostRows(page, m.Width - 4, height - 2));

    private static List<IRenderable> CostRows(CostPage page, int width, int capacity)
    {
        var rows = new List<IRenderable>();
        // A failed read keeps the last report under its one-line notice, as a snapshot section keeps the last data.
        if (page.Problem is { } problem) rows.Add(new Line().Flex(problem, Theme.Of(Theme.Held)).Build(width));
        if (page.Report is not { } report)
        {
            if (page.Problem is null) rows.Add(new Line().Flex("Reading the cost.", Theme.Of(Theme.Dim)).Build(width));
            return rows;
        }
        if (report.NothingRecorded)
        {
            rows.Add(new Line().Flex(CostEmpty, Theme.Of(Theme.Soft)).Build(width));
            return rows;
        }

        rows.AddRange(LifetimeRows(report.Lifetime, width));
        rows.Add(new Line().Build(width));

        var runs = report.Runs;
        var run = page.SelectedRun;
        var videos = run?.Videos ?? [];
        // What the two lists share: the rows above, the runs' head and column heads, the blank under the runs, the videos' head
        // and the footnote are fixed. The runs list takes up to half when both lists are too long for the room.
        var room = capacity - rows.Count - 5;
        var runRows = Math.Max(1, runs.Count);
        var videoRows = Math.Max(1, videos.Count);
        if (runRows + videoRows > room)
        {
            runRows = Math.Clamp(runRows, 1, Math.Max(1, Math.Max(room / 2, room - videoRows)));
            videoRows = Math.Max(1, room - runRows);
        }

        if (runs.Count == 0)
        {
            rows.Add(Head("Runs", string.Empty, width));
            rows.Add(new Line().Left("  ", Style.Plain).Flex("No run has used a Hetzner worker yet.", Theme.Of(Theme.Dim)).Build(width));
        }
        else
        {
            rows.Add(Head("Runs, newest first", runs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), width));
            rows.AddRange(RunRows(runs, page.Selected, width, runRows));
            rows.Add(new Line().Build(width));
            rows.AddRange(VideoRows(run!, page.VideoScroll, width, videoRows));
        }

        // The footnote is pinned to the panel's last row; a window too short for everything keeps the top rows and the footnote.
        var footnote = new Line().Flex(CostFootnote, Theme.Of(Theme.Dim)).Build(width);
        if (capacity < 2) return [.. rows.Take(capacity)];
        rows = [.. rows.Take(capacity - 1)];
        while (rows.Count < capacity - 1) rows.Add(new Line().Build(width));
        rows.Add(footnote);
        return rows;
    }

    /// <summary>The figure, what Hetzner billed and what it freed, and why the figure may still change or leave something out.</summary>
    private static IEnumerable<IRenderable> LifetimeRows(CostLifetime lifetime, int width)
    {
        var tags = new[] { lifetime.StillBilling ? "still billing" : null, lifetime.EndEstimated ? "end estimated" : null }.OfType<string>().ToArray();
        var headline = new Line().Left("All time: ", Theme.Of(Theme.Soft)).Flex(CostCommand.LifetimeFigure(lifetime), Theme.Of(Theme.Ready, bold: true));
        if (tags.Length > 0) headline.Right(string.Join(" · ", tags) + " ", Theme.Of(Theme.Held));
        yield return headline.Build(width);
        yield return new Line().Flex(CostCommand.Bill(lifetime), Theme.Of(Theme.Text)).Build(width);
        foreach (var note in CostCommand.Notes(lifetime)) yield return new Line().Flex(note, Theme.Of(Theme.Dim)).Build(width);
    }

    private static IRenderable Head(string title, string count, int width) =>
        new Line().Left(title, Theme.Of(Theme.Accent, bold: true)).Left(count.Length > 0 ? Gap + count : string.Empty, Theme.Of(Theme.Dim)).Build(width);

    /// <summary>
    /// One row per run, with its columns padded across every run, not only the ones in view, so they stay put as the cursor
    /// moves. The row is one cut-to-fit text after its time, so a narrow window cuts it with an ellipsis instead of wrapping.
    /// </summary>
    private static List<IRenderable> RunRows(IReadOnlyList<CostRun> runs, int selected, int width, int capacity)
    {
        var waiting = runs.Any(run => run.WaitingBytes > 0);
        string[] Cells(CostRun run) =>
        [
            run.WorkerName,
            run.Eur is { } eur ? CostPrice.Amount(eur) : "no price",
            run.FreedBytes > 0 ? TuiText.Size(run.FreedBytes) : "-",
            .. waiting ? [run.WaitingBytes > 0 ? TuiText.Size(run.WaitingBytes) : "-"] : Array.Empty<string>(),
            CostPrice.PerTerabyte(run.EurPerTerabyte)
        ];
        string[] head = ["Worker", "EUR", "Freed", .. waiting ? ["Waiting for review"] : Array.Empty<string>(), "EUR per TB"];
        var whens = runs.Select(run => CostCommand.When(run.StartedUtc, run.EndUtc)).ToArray();
        var cells = runs.Select(Cells).ToArray();
        var whenWidth = Math.Max("When".Length, whens.Max(TuiText.Cells));
        var widths = head.Select((title, column) => Math.Max(TuiText.Cells(title), cells.Max(row => TuiText.Cells(row[column])))).ToArray();
        // The worker's name reads from the left; the figures line up on the right of their columns.
        string Columns(string[] row) => string.Join(Gap, row.Select((cell, column) => column == 0 ? cell.PadRight(widths[column]) : cell.PadLeft(widths[column])));

        var rows = new List<IRenderable>
        {
            new Line().Left("  " + "When".PadRight(whenWidth) + Gap, Theme.Of(Theme.Dim)).Flex(Columns(head), Theme.Of(Theme.Dim)).Build(width)
        };
        var start = Bars.WindowStart(runs.Count, selected, capacity);
        for (var at = start; at < Math.Min(runs.Count, start + capacity); at++)
        {
            var chosen = at == selected;
            rows.Add(new Line()
                .Left(chosen ? "▌ " : "  ", Theme.Of(Theme.Accent))
                .Left(whens[at].PadRight(whenWidth) + Gap, Theme.Of(Theme.Soft))
                .Flex(Columns(cells[at]), Theme.Of(Theme.Text, bold: chosen))
                .Build(width, chosen ? Theme.Selection : null));
        }
        return rows;
    }

    /// <summary>
    /// The selected run's videos, scrolled by the page keys. A video that saved nothing says why in the Saved column, since its
    /// worker time still counts.
    /// </summary>
    private static List<IRenderable> VideoRows(CostRun run, int scroll, int width, int capacity)
    {
        var videos = run.Videos;
        var shown = capacity;
        var start = Math.Clamp(scroll, 0, Math.Max(0, videos.Count - shown));
        var range = videos.Count > shown ? $"{start + 1}-{Math.Min(videos.Count, start + shown)} of {videos.Count}" : videos.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var rows = new List<IRenderable> { Head("Videos in this run", range, width) };
        if (videos.Count == 0)
        {
            rows.Add(new Line().Left("  ", Style.Plain).Flex("No videos count toward this run.", Theme.Of(Theme.Dim)).Build(width));
            return rows;
        }

        string[] Cells(CostVideo video) =>
        [
            HumanReadableValues.FormatDuration(video.WorkerTime),
            video.Eur is { } eur ? CostPrice.Amount(eur) : "no price",
            Saved(video),
            CostPrice.PerTerabyte(video.EurPerTerabyte)
        ];
        var cells = videos.Select(Cells).ToArray();
        var widths = Enumerable.Range(0, 4).Select(column => cells.Max(row => TuiText.Cells(row[column]))).ToArray();
        foreach (var (video, at) in videos.Select((video, at) => (video, at)).Skip(start).Take(shown))
        {
            var right = string.Join(Gap, cells[at].Select((cell, column) => column == 2 ? cell.PadRight(widths[column]) : cell.PadLeft(widths[column]))) + " ";
            var line = new Line().Left("  ", Style.Plain);
            // Too narrow for the name beside the figures: one text, cut where the window ends.
            if (TuiText.Cells(right) + 12 > width) line.Flex(video.FileName + Gap + right, Theme.Of(Theme.Text));
            else line.Flex(video.FileName, Theme.Of(Theme.Text)).Right(right, Theme.Of(video.State == CostVideoState.NothingSaved ? Theme.Dim : Theme.Soft));
            rows.Add(line.Build(width));
        }
        return rows;
    }

    private static string Saved(CostVideo video) => video.State switch
    {
        CostVideoState.Freed => $"{TuiText.Size(video.SavedBytes)} freed",
        CostVideoState.WaitingForReview => $"{TuiText.Size(video.SavedBytes)} waiting for review",
        _ => $"nothing saved: {NothingSavedBecause(video.Status)}"
    };

    /// <summary>Why a video with worker time freed nothing, in the queue's words for its status.</summary>
    private static string NothingSavedBecause(JobStatus status) => status switch
    {
        JobStatus.Failed => "failed",
        JobStatus.Skipped => "not worth converting",
        JobStatus.Rejected => "rejected",
        JobStatus.Cancelled => "dropped",
        JobStatus.Interrupted => "stopped",
        JobStatus.Queued => "in the queue",
        JobStatus.Finalizing => "finalising",
        _ => "converting"
    };
}
