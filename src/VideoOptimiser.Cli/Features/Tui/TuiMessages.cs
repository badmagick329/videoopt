using System.Collections.Immutable;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Cost;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Cli.Features.Tui;

// Everything that can change the model arrives as one of these through one channel.

internal abstract record TuiMsg;

internal sealed record KeyMsg(ConsoleKeyInfo Key) : TuiMsg;

/// <summary>About four a second. Carries the clock and the window size, so a resize is just another message.</summary>
internal sealed record TickMsg(DateTimeOffset Now, int Width, int Height) : TuiMsg;

/// <summary>What the feed read from the database, the lock files, the state file and the event file. A section is null when its read failed.</summary>
internal sealed record SnapshotMsg(TuiSnapshot Snapshot) : TuiMsg;

/// <summary>Hetzner's answer to <see cref="RefreshWorkerCheck"/>, or the reason it could not be asked.</summary>
internal sealed record WorkerCheckedMsg(int Generation, WorkerCheck Check) : TuiMsg;

/// <summary>One line an action wants in the log.</summary>
internal sealed record LogLine(LogKind Kind, string Text);

/// <summary>What an effect did, in words: log lines, a short notice for the footer, and whether the worker may have changed.</summary>
/// <param name="RefreshWorker">The action touched the worker, so what Hetzner said earlier is stale and is asked again.</param>
internal sealed record ActionResult(IReadOnlyList<LogLine> Lines, string? Notice = null, bool RefreshWorker = false)
{
    public static ActionResult Say(LogKind kind, string text, bool refreshWorker = false) => new([new LogLine(kind, text)], text, refreshWorker);
}

/// <summary>An effect ended; <paramref name="Key"/> is its <see cref="TuiEffect.BusyKey"/>, which is free again.</summary>
internal sealed record ActionDoneMsg(string Key, ActionResult Result) : TuiMsg;

/// <summary>A line an effect reports while it works (creating or deleting the worker), so a long action shows its progress.</summary>
internal sealed record TuiLogMsg(LogKind Kind, string Text) : TuiMsg;

/// <summary>A finalise batch started on <paramref name="Id"/>: Review tags its row as the one under way.</summary>
internal sealed record FinaliseStartedMsg(Guid Id) : TuiMsg;

/// <summary>
/// A finalise batch ended <paramref name="Id"/>, finalised or not. Its log line comes now rather than with the batch's
/// <see cref="ActionDoneMsg"/>, so a long batch shows each video as it ends.
/// </summary>
/// <param name="SavedBytes">What finalising it saved; 0 when it failed or a size is unknown.</param>
internal sealed record FinaliseEndedMsg(Guid Id, bool Succeeded, long SavedBytes, LogLine Line) : TuiMsg;

/// <summary>The cost report <see cref="ReadCost"/> read, or why it could not be read (<paramref name="Report"/> null).</summary>
internal sealed record CostReadMsg(CostReport? Report, string? Problem) : TuiMsg;

internal enum WorkerOptionsPurpose { Create, Start }

/// <summary>The servers a creation may choose from, or why they could not be listed (<paramref name="Error"/> may hold several lines).</summary>
internal sealed record WorkerOptionsMsg(WorkerOptionsPurpose Purpose, IReadOnlyList<RemoteWorkerCreationOption>? Options, string? Error) : TuiMsg;

/// <summary>A dialog an effect prepared from data it had to read (finalising shows what the confirmation shows), or why there is none.</summary>
/// <param name="Lines">For the log, when the footer's one line can't hold everything: <c>F</c> with only warned videos names them there.</param>
internal sealed record PreparedMsg(string Key, TuiConfirm? Confirm, string? Notice, IReadOnlyList<LogLine>? Lines = null) : TuiMsg;

/// <summary>
/// A thing Update asks the world to do. Update stays pure by returning these instead of doing them; <see cref="TuiEffects"/>
/// runs each off the loop thread and posts what came of it back as a message.
/// </summary>
internal abstract record TuiEffect
{
    /// <summary>
    /// What an effect holds while it runs: a second effect with the same key is refused with the first one's text, so two
    /// presses never start the same thing twice. Null for an effect that may overlap.
    /// </summary>
    public virtual string? BusyKey => null;

    /// <summary>What the footer says while the effect is out, such as "Creating the worker.".</summary>
    public virtual string? BusyText => null;
}

/// <summary>What the quit dialog's y answers: leave now, cutting off what is still going. Update handles it; nothing runs it.</summary>
internal sealed record QuitNow : TuiEffect;

/// <summary>Ask Hetzner about the managed worker (up to 15 seconds), for the answer that goes with <paramref name="Generation"/>.</summary>
internal sealed record RefreshWorkerCheck(int Generation) : TuiEffect;

/// <summary>Read the snapshot now, after an action changed something, instead of waiting for the feed's next second.</summary>
internal sealed record RefreshSnapshot : TuiEffect;

/// <summary>
/// Read the cost report for the cost page. A read like the snapshot: it holds no busy key, so it never makes quitting ask, and
/// a failure comes back as <see cref="CostReadMsg.Problem"/> instead of an error line in the log.
/// </summary>
internal sealed record ReadCost : TuiEffect;

internal enum QueueAction { Hold, Release, Next, Drop, Retry, Reject, Dismiss }

internal sealed record QueueActionEffect(QueueAction Action, Guid Id, string Name) : TuiEffect
{
    public override string BusyKey => $"queue:{Id:N}";

    public override string BusyText => Action switch
    {
        QueueAction.Hold => $"Holding {Name}.",
        QueueAction.Release => $"Releasing {Name}.",
        QueueAction.Next => $"Moving {Name} to the front.",
        QueueAction.Drop => $"Dropping {Name}.",
        QueueAction.Reject => $"Rejecting {Name}.",
        QueueAction.Dismiss => $"Dismissing {Name}.",
        _ => $"Queueing {Name} again."
    };
}

/// <summary><c>queue dismiss --all</c>: every failure not dismissed yet, not only the ones the Failed group shows.</summary>
internal sealed record DismissAllFailures : TuiEffect
{
    public override string BusyKey => "dismiss";
    public override string BusyText => "Dismissing the failures.";
}

/// <summary>List the servers a creation may choose from; Hetzner is asked, so it runs off the loop thread.</summary>
internal sealed record LoadWorkerOptions(WorkerOptionsPurpose Purpose) : TuiEffect
{
    public override string BusyKey => "worker";
    public override string BusyText => "Asking Hetzner for the servers.";
}

internal sealed record CreateWorker(RemoteWorkerCreationOption Option) : TuiEffect
{
    public override string BusyKey => "worker";
    public override string BusyText => "Creating the worker.";
}

internal sealed record DeleteWorker : TuiEffect
{
    public override string BusyKey => "worker";
    public override string BusyText => "Deleting the worker.";
}

/// <summary>What a started run needs, all answered before it launches: a server to create first (null: none), the worker's fate and the mode flag (null: the config's).</summary>
internal sealed record StartPlan(RemoteWorkerCreationOption? Create, WorkerFateChoice Fate, ProcessingModeOverride? Mode);

internal sealed record StartRun(StartPlan Plan) : TuiEffect
{
    public override string BusyKey => "run";
    public override string BusyText => Plan.Create is null ? "Starting the run." : "Creating the worker, then starting the run.";
}

internal sealed record StopRun(bool Now) : TuiEffect
{
    public override string BusyKey => "stop";
    public override string BusyText => Now ? "Asking the run to stop now." : "Asking the run to stop after this video.";
}

internal sealed record SetWorkerFate(WorkerFateChoice Fate) : TuiEffect
{
    public override string BusyKey => "fate";
    public override string BusyText => "Changing what happens to the worker.";
}

internal sealed record OpenFile(Guid Id, bool Original, string Name) : TuiEffect;

/// <summary>Read what is ready to finalise and prepare the confirmation; <paramref name="Id"/> null means every ready video, as <c>finalize --ready</c>.</summary>
internal sealed record PrepareFinalise(Guid? Id) : TuiEffect
{
    public override string BusyKey => "finalise";
    public override string BusyText => "Reading what is ready to finalise.";
}

/// <param name="LeftOut">
/// The names of the warned videos <c>F</c> left out, said again after the tally so they are the last thing read; empty for <c>f</c>.
/// </param>
internal sealed record FinaliseVideos(IReadOnlyList<Guid> Ids, IReadOnlyList<string>? LeftOut = null) : TuiEffect
{
    public override string BusyKey => "finalise";
    public override string BusyText => "Finalising.";
}

/// <summary>
/// Esc during a finalise batch: stop before the next video. It holds no key, since it only signals the finalise that holds
/// <c>finalise</c>; the video being finalised always finishes.
/// </summary>
internal sealed record StopFinalise : TuiEffect;

/// <summary>The model after a message, and what it asks to be done.</summary>
internal readonly record struct TuiStep(TuiModel Model, ImmutableArray<TuiEffect> Effects)
{
    public static TuiStep Of(TuiModel model) => new(model, []);
}

/// <summary>The recorded worker section of a snapshot.</summary>
/// <param name="Managed">The lifecycle is Hetzner: there is a worker to ask about.</param>
internal sealed record WorkerSection(bool Managed, string Lifecycle, RemoteWorkerInfo? Recorded);

/// <param name="Holder">The run going now, or null when none is.</param>
/// <param name="Requests">What another process asked of it (stop, fate); null with no run.</param>
internal sealed record RunSection(LiveRunHolder? Holder, RunRequests? Requests);

/// <summary>
/// One read of everything the screen shows. A section whose read threw is null, and the model keeps what it had for it:
/// a locked database for a moment must not blank the queue.
/// </summary>
/// <param name="Events">Every event of the current or last run; the model keeps its own count and takes only the new ones.</param>
/// <param name="Problems">One line per section that could not be read.</param>
internal sealed record TuiSnapshot(
    ImmutableList<Video>? Open,
    ImmutableList<Video>? Failed,
    RunSection? Run,
    WorkerSection? Worker,
    IReadOnlyList<RunEvent>? Events,
    IReadOnlyList<string> Problems);
