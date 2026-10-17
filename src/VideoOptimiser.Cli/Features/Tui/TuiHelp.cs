namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>One key and what it does, in the footers' words; a key that only applies in a state says which.</summary>
internal sealed record HelpKey(string Key, string What);

internal sealed record HelpGroup(string Title, IReadOnlyList<HelpKey> Keys);

/// <summary>
/// The key guide the <c>?</c> overlay draws: every key, grouped as the footers group them. A key that works only in some state
/// (the stop keys while a run is going, the worker keys with a managed worker) says so in its description instead of hiding.
/// Wording goes through the humanizer gate (docs index); keep it in step with the footers in <see cref="TuiView"/>.
/// </summary>
internal static class TuiHelp
{
    public static IReadOnlyList<HelpGroup> Groups { get; } =
    [
        new("Anywhere",
        [
            new("tab / shift+tab", "next or previous pane"),
            new("s", "start a run (when none is going and videos are waiting)"),
            // One row for both stop keys keeps every group in a 40-row window.
            new("x / X", "stop the run after this video, or now after asking (while a run is going)"),
            new(TuiKeys.Name(TuiKeys.CostPage), "what the freed space cost on Hetzner workers"),
            new("?", "this help"),
            new("q / Ctrl+C", "quit; asks first if an action is still going")
        ]),
        new("Queue",
        [
            new("↑↓", "select a video"),
            new("h", "hold a waiting or stopped video"),
            new("r", "release a held video"),
            new("n", "run a video next"),
            new("d", "drop a video from the queue, or dismiss a failed one, after asking"),
            new("t", "queue a failed video again, after asking")
        ]),
        new("Worker",
        [
            new("c", "create a worker (managed worker only)"),
            new("d", "delete the worker, after asking (managed worker only)"),
            new("r", "check the worker with Hetzner now (managed worker only)")
        ]),
        new("Activity",
        [
            new("↑↓ pgup pgdn", "scroll the log"),
            new("end", "follow the log"),
            new("w", "delete or keep the worker when the run ends (background run, managed worker)")
        ]),
        new("Review",
        [
            new("↑↓", "select a ready video"),
            new("o", "open the original"),
            new("a", "open the AV1"),
            new("f", "finalise the selected video, after asking"),
            new("F", "finalise every ready video without warnings, after asking"),
            new("r", "reject the selected video, deleting its AV1, after asking"),
            new("esc", "stop finalising after the video it's on (while several are being finalised)")
        ])
    ];
}
