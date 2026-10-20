namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// One action's key as the three places that show or read it need it: the characters <see cref="TuiUpdate"/> matches, the
/// footer's hint in <see cref="TuiView"/>, and the key guide's row in <see cref="TuiHelp"/>. Keys match on
/// <see cref="ConsoleKeyInfo.KeyChar"/>, so a character works on any layout that can type it.
/// </summary>
/// <param name="Chars">What the key answers to. Empty for a key matched on <see cref="ConsoleKey"/> (arrows, tab, end, esc),
/// whose handling stays in <see cref="TuiUpdate"/>; the binding then only names it for the footer and the guide.</param>
/// <param name="Footer">The footer's word for it; empty shows the key alone, null leaves it out of every footer.</param>
/// <param name="Help">The guide's description. Null puts the key on the guide row of the key before it, with that row's words:
/// how <c>x</c> and <c>X</c> share one row and keep every group in a 40-row window.</param>
/// <param name="Label">How the footer names it, when its characters don't (<c>q</c>, which also answers to <c>Q</c>).</param>
/// <param name="GuideLabel">How the guide names it, when the footer's name is too short for it (<c>q / Ctrl+C</c>).</param>
internal sealed record TuiKey(IReadOnlyList<char> Chars, string? Footer, string? Help, string? Label = null, string? GuideLabel = null)
{
    public string Name => Label ?? TuiKeys.Name(Chars);

    public string GuideName => GuideLabel ?? Name;

    public bool Is(char c) => Chars.Contains(c);

    /// <summary>The footer's hint; only a key with a footer word has one.</summary>
    public (string Key, string What) Hint => (Name, Footer ?? throw new InvalidOperationException($"{Name} has no footer hint."));

    /// <summary>The footer's hint with another word, for a key whose action depends on what is selected (<c>d</c> on a failed video).</summary>
    public (string Key, string What) HintAs(string what) => (Name, what);
}

/// <summary>A pane's keys in the key guide's order, under the guide's title for them.</summary>
internal abstract class PaneKeys(string title)
{
    public string Title { get; } = title;

    public abstract IReadOnlyList<TuiKey> All { get; }
}

/// <summary>
/// Every key of the full-screen view, one table per pane, so a key's characters, footer word and guide line change in one
/// edit (the user's call, 2026-10-17, for the cost page; VOPT-176 for the rest). The footers in <see cref="TuiView"/> pick
/// which hints show in which state and <see cref="TuiUpdate"/> keeps what a key does; this table only names each key. Wording
/// goes through the humanizer gate (docs index).
/// </summary>
internal static class TuiKeys
{
    /// <summary>Opens and closes the cost page (epic VOPT-126).</summary>
    public static IReadOnlyList<char> CostPage { get; } = ['$'];

    /// <summary>The set as the key guide and the footer name it, such as <c>$</c> or <c>$ / m</c>.</summary>
    public static string Name(IReadOnlyList<char> keys) => string.Join(" / ", keys);

    public static AnywhereKeys Anywhere { get; } = new();
    public static QueueKeys Queue { get; } = new();
    public static WorkerKeys Worker { get; } = new();
    public static ActivityKeys Activity { get; } = new();
    public static ReviewKeys Review { get; } = new();

    /// <summary>The tables in the key guide's order.</summary>
    public static IReadOnlyList<PaneKeys> Panes { get; } = [Anywhere, Queue, Worker, Activity, Review];

    /// <summary>The keys that work from every pane: the run's, the screen's and quitting.</summary>
    internal sealed class AnywhereKeys() : PaneKeys("Anywhere")
    {
        public TuiKey NextPane { get; } = new([], "", "next or previous pane", Label: "tab", GuideLabel: "tab / shift+tab");
        public TuiKey Start { get; } = new(['s'], "start run", "start a run (when none is going and videos are waiting)");
        public TuiKey StopAfter { get; } = new(['x'], "stop after", "stop after this video (press again to carry on), or now after asking (while a run is going)");
        public TuiKey StopNow { get; } = new(['X'], "stop now", null);
        public TuiKey Cost { get; } = new(CostPage, null, "what the freed space cost on Hetzner workers");
        public TuiKey Guide { get; } = new(['?'], "help", "this help");
        public TuiKey Quit { get; } = new(['q', 'Q'], "quit", "quit; asks first if an action is still going", Label: "q", GuideLabel: "q / Ctrl+C");

        public override IReadOnlyList<TuiKey> All => [NextPane, Start, StopAfter, StopNow, Cost, Guide, Quit];
    }

    internal sealed class QueueKeys() : PaneKeys("Queue")
    {
        public TuiKey Select { get; } = new([], "select", "select a video", Label: "↑↓");
        public TuiKey Hold { get; } = new(['h'], "hold", "hold a waiting or stopped video");
        public TuiKey Release { get; } = new(['r'], "release", "release a held video");
        public TuiKey RunNext { get; } = new(['n'], "run next", "run a video next");
        public TuiKey Drop { get; } = new(['d'], "drop", "drop a video from the queue, or dismiss a failed one, after asking");
        public TuiKey Retry { get; } = new(['t'], "retry", "queue a failed video again, after asking");

        /// <summary>The footer's word for <see cref="Drop"/> on a failed video, which it dismisses instead (VOPT-115).</summary>
        public const string DismissFooter = "dismiss";

        public override IReadOnlyList<TuiKey> All => [Select, Hold, Release, RunNext, Drop, Retry];
    }

    internal sealed class WorkerKeys() : PaneKeys("Worker")
    {
        public TuiKey Create { get; } = new(['c'], "create", "create a worker (managed worker only)");
        public TuiKey Delete { get; } = new(['d'], "delete", "delete the worker, after asking (managed worker only)");
        public override IReadOnlyList<TuiKey> All => [Create, Delete];
    }

    internal sealed class ActivityKeys() : PaneKeys("Activity")
    {
        public TuiKey Scroll { get; } = new([], "scroll", "scroll the log", Label: "↑↓", GuideLabel: "↑↓ pgup pgdn");
        public TuiKey Follow { get; } = new([], "follow", "follow the log", Label: "end");
        public TuiKey Fate { get; } = new(['w'], "worker fate", "delete or keep the worker when the run ends (background run, managed worker)");

        public override IReadOnlyList<TuiKey> All => [Scroll, Follow, Fate];
    }

    internal sealed class ReviewKeys() : PaneKeys("Review")
    {
        public TuiKey Select { get; } = new([], "select", "select a ready video", Label: "↑↓");
        public TuiKey OpenOriginal { get; } = new(['o'], "original", "open the original");
        public TuiKey OpenAv1 { get; } = new(['a'], "AV1", "open the AV1");
        public TuiKey Finalise { get; } = new(['f'], "finalise", "finalise the selected video, after asking");
        public TuiKey FinaliseAll { get; } = new(['F'], "all", "finalise every ready video without warnings, after asking");
        public TuiKey Reject { get; } = new(['r'], "reject", "reject the selected video, deleting its AV1, after asking");

        /// <summary>Esc works from every pane with no dialog open, but the batch shows in Review, so its footer and guide group hold it.</summary>
        public TuiKey StopFinalise { get; } = new([], "stop", "stop finalising after the video it's on (while several are being finalised)", Label: "esc");

        public override IReadOnlyList<TuiKey> All => [Select, OpenOriginal, OpenAv1, Finalise, FinaliseAll, Reject, StopFinalise];
    }
}
