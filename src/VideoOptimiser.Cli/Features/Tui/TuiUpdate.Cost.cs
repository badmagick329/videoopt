namespace VideoOptimiser.Cli.Features.Tui;

// The cost page (epic VOPT-126): what the space freed on Hetzner workers cost. It is out of the way on purpose (the user's
// decision): a key opens it over the panes, it takes the keys while open, and it reads its report only while it is open.

internal static partial class TuiUpdate
{
    /// <summary>
    /// How often an open page reads the report again. A run on the worker moves the figures by fractions of a cent a minute,
    /// so a few seconds is fresh enough, and each read goes over every run record.
    /// </summary>
    internal static readonly TimeSpan CostEvery = TimeSpan.FromSeconds(5);

    /// <summary>How many videos a page key scrolls the selected run's list by.</summary>
    private const int VideoPage = 5;

    private static TuiStep OpenCost(TuiModel m) =>
        new(m with { Cost = new CostPage(null, null, 0, Reading: true, AskedAt: m.Now, Problem: null) }, [new ReadCost()]);

    /// <summary>A read waits for the one before it, so a slow database never stacks reads up.</summary>
    private static TuiStep ReadCostWhenDue(TuiModel m)
    {
        if (m.Cost is not { Reading: false } page || m.Now - page.AskedAt < CostEvery) return TuiStep.Of(m);
        return new(m with { Cost = page with { Reading = true, AskedAt = m.Now } }, [new ReadCost()]);
    }

    /// <summary>A failed read keeps the last report and says why; an answer for a page closed since is dropped.</summary>
    private static TuiModel OnCostRead(TuiModel m, CostReadMsg read)
    {
        if (m.Cost is not { } page) return m;
        return m with { Cost = page with { Report = read.Report ?? page.Report, Problem = read.Problem, Reading = false } };
    }

    /// <summary>
    /// The page's keys: its own set and Esc close it; arrows and Home/End select a run; the page keys scroll that run's videos.
    /// Every other key does nothing, as on the key guide, so nothing acts on a pane the page hides.
    /// </summary>
    private static TuiStep CostKey(TuiModel m, CostPage page, ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape || TuiKeys.CostPage.Contains(key.KeyChar)) return TuiStep.Of(m with { Cost = null });
        var runs = page.Report?.Runs ?? [];
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Home or ConsoleKey.End when runs.Count > 0:
                var at = key.Key switch
                {
                    ConsoleKey.UpArrow => page.Selected - 1,
                    ConsoleKey.DownArrow => page.Selected + 1,
                    ConsoleKey.Home => 0,
                    _ => runs.Count - 1
                };
                var run = runs[Math.Clamp(at, 0, runs.Count - 1)];
                // Another run's videos start from the top.
                return TuiStep.Of(m with { Cost = page with { Run = run.Id, VideoScroll = run.Id == page.SelectedRun?.Id ? page.VideoScroll : 0 } });
            case ConsoleKey.PageUp or ConsoleKey.PageDown:
                var videos = page.SelectedRun?.Videos.Count ?? 0;
                var scroll = page.VideoScroll + (key.Key == ConsoleKey.PageDown ? VideoPage : -VideoPage);
                return TuiStep.Of(m with { Cost = page with { VideoScroll = Math.Clamp(scroll, 0, Math.Max(0, videos - 1)) } });
            default:
                return TuiStep.Of(m);
        }
    }
}
