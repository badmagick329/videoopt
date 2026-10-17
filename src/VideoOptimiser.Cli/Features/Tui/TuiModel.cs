using System.Collections.Immutable;
using VideoOptimiser.Application.Features.Cost;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Tui;

// The whole screen state lives in one immutable TuiModel. TuiUpdate.Update(model, msg) is the only thing that produces a new
// one and TuiView is a pure function of it, as in Bubble Tea. Time and terminal size enter through TickMsg, never by reading
// the clock inside Update or View, so a recorded run of messages can be replayed to the same screens.

internal enum Pane { Queue, Worker, Activity, Review }

/// <summary>
/// Where a video sits in the queue pane, in display order. The first four are <see cref="Application.Features.Queue.OpenJobGroup"/>;
/// Failed is the history's latest failures, the target of a later retry.
/// </summary>
internal enum VideoGroup { ConvertingNow, Queue, Held, Ready, Failed }

/// <summary>The stages a video passes through, named as the glossary names the job statuses.</summary>
internal enum Stage { Uploading, FindingCrf, Encoding, Downloading, Checking }

/// <summary>How a log line reads. <see cref="Detail"/> sits under the step it belongs to, as a CRF attempt does in the console.</summary>
internal enum LogKind { Info, Stage, Success, Warning, Error, Action, Detail }

/// <param name="Id">The job's ID: the selection follows it when the list changes under the cursor.</param>
/// <param name="SizeBytes">The original's size; null when no probe recorded it.</param>
/// <param name="Remote">The video converts on a worker, so it has an upload and a download stage.</param>
/// <param name="Stopped">A stop left it, so a run takes it up from its stage.</param>
/// <param name="Note">A phrase for the row's right side: why a video failed, or what a ready video's finalising did.</param>
/// <param name="Immich">An Immich video: its original is in Immich, and the AV1 sits beside a downloaded copy.</param>
/// <param name="Warnings">What validation warned about a ready video, one sentence each: <c>F</c> leaves it out, so it is watched and finalised with <c>f</c>.</param>
internal sealed record Video(
    Guid Id,
    string Name,
    VideoGroup Group,
    long? SizeBytes,
    JobStatus Status,
    bool Remote = false,
    bool Stopped = false,
    long? Av1Bytes = null,
    decimal? PercentSaved = null,
    string? Note = null,
    bool Immich = false,
    IReadOnlyList<string>? Warnings = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = Warnings ?? [];

    public long? SavedBytes => SizeBytes is { } size && Av1Bytes is { } av1 ? size - av1 : null;
}

internal sealed record LogEntry(DateTimeOffset At, LogKind Kind, string Text);

internal sealed record Notice(string Text, DateTimeOffset Until);

/// <summary>A question the user answers with y; anything else cancels. What <paramref name="OnYes"/> asks for runs as an effect.</summary>
internal sealed record TuiConfirm(string Question, string Detail, TuiEffect OnYes);

/// <summary>
/// One line of a list dialog. Choosing it runs <paramref name="Effect"/>, or opens <paramref name="Next"/> (the start flow asks
/// the worker, then what happens to it, before anything launches).
/// </summary>
/// <param name="Hint">Dim text at the line's right, such as a server's price.</param>
internal sealed record ChoiceOption(string Label, TuiEffect? Effect, TuiChoice? Next = null, string Hint = "");

/// <summary>A list the user picks from: arrows move, Enter chooses, Esc cancels.</summary>
internal sealed record TuiChoice(string Title, string Detail, ImmutableList<ChoiceOption> Options, int Selected);

/// <summary>Where the run is in its flow, from its events: the idle text says why nothing converts.</summary>
internal enum RunPhase { Preparing, Converting, Ending }

/// <summary>What the run's event file has told the model so far.</summary>
/// <param name="EventsSeen">How many of the file's events are in the log; only later ones are new.</param>
/// <param name="Started">The file's first event; another one means another run, and the log starts again.</param>
/// <param name="ProgressText">The latest detail or tool line of the video converting now.</param>
/// <param name="ProgressPercent">The progress line's reading, for its bar; null when it is not a reading.</param>
/// <param name="WaitingForWorker">The run reported creating or bootstrapping the worker and has not started converting.</param>
/// <param name="LastEventAt">When the run last reported anything: a converting video that has been quiet for long shows how long.</param>
/// <param name="LastAnswerAt">When the worker last answered a poll of a quiet stage, which tells a slow step from a lost worker.</param>
internal sealed record ActivityState(
    int EventsSeen,
    RunEvent? Started,
    string? ProgressText,
    RunPhase Phase,
    bool WaitingForWorker,
    int? ProgressPercent = null,
    DateTimeOffset? LastEventAt = null,
    DateTimeOffset? LastAnswerAt = null)
{
    public static ActivityState None { get; } = new(0, null, null, RunPhase.Preparing, false);
}

/// <summary>
/// The managed worker as the model knows it: whether there is one to ask about, what the local state file records (read every
/// refresh), and Hetzner's latest answer (asked at the start and every 30 seconds, off the loop thread).
/// </summary>
/// <param name="Lifecycle">The configured <c>processing.remoteSsh.lifecycle</c>.</param>
/// <param name="Generation">Counts the times the recorded worker changed; an answer to an older question is dropped.</param>
/// <param name="CheckAskedAt">When the latest question went out; null asks again at the next tick.</param>
/// <param name="AskedByHand">The person pressed <c>r</c> and the footer owes them the answer, however long Hetzner takes.</param>
internal sealed record WorkerState(
    bool Managed,
    string Lifecycle,
    RemoteWorkerInfo? Recorded,
    WorkerCheck? Check,
    bool CheckInFlight,
    DateTimeOffset? CheckAskedAt,
    int Generation,
    bool AskedByHand)
{
    public static WorkerState Unknown { get; } = new(false, string.Empty, null, null, false, null, 0, false);
}

/// <summary>
/// A finalise that is out, as its progress messages tell it. Finalising a batch takes minutes (each original is checked, then
/// replaced or uploaded), and the snapshot only shows the one video in status Finalizing, so without this Review could not say
/// how far the batch is or which videos are still to come. Arrows and the other keys keep working: this is progress, not a lock.
/// </summary>
/// <param name="Ids">The videos in the order <see cref="TuiActions.FinaliseAsync"/> takes them.</param>
/// <param name="Current">The video being finalised now; null between two videos and before the first starts.</param>
/// <param name="Ended">How many have ended, finalised or failed: the first <paramref name="Ended"/> of <paramref name="Ids"/>.</param>
/// <param name="SavedBytes">What the finalised ones saved, the same figure Review's <c>would save</c> adds up.</param>
/// <param name="StopAsked">Esc asked the batch to stop after the video it is on; that video always finishes.</param>
internal sealed record FinaliseBatch(ImmutableList<Guid> Ids, Guid? Current, int Ended, int Failed, long SavedBytes, bool StopAsked = false)
{
    /// <summary>The videos not ended yet, the current one included: with one left a stop has nothing to save.</summary>
    public int Left => Ids.Count - Ended;

    /// <summary>Esc would stop something: more than the current video is left and no stop is asked yet.</summary>
    public bool CanStop => Left > 1 && !StopAsked;

    /// <summary>A batch of one (<c>f</c>) shows its row's tag and the footer only: <c>1 of 1</c> says nothing.</summary>
    public bool Counts => Ids.Count > 1;

    /// <summary>The position Review's title and summary show: the video under way, or the last once all have ended.</summary>
    public int Position => Math.Min(Ended + 1, Ids.Count);

    public bool IsWaiting(Guid id) => id != Current && Ids.Skip(Ended).Contains(id);
}

/// <summary>
/// The cost page (<see cref="TuiKeys.CostPage"/>) while it is open. It takes the keys as the key guide does, while runs,
/// finalising and the feed go on updating the rest of the model underneath. Its report is read only while it is open, never by
/// the once-a-second snapshot: the cost reads every run record, and nobody needs it while converting.
/// </summary>
/// <param name="Report">The last report read; null until the first read answers.</param>
/// <param name="Run">The selected run's ID, so the cursor stays on it when a new report adds a run above it.</param>
/// <param name="VideoScroll">How many of the selected run's videos are scrolled past.</param>
/// <param name="Reading">A read is out; the next waits for it.</param>
/// <param name="AskedAt">When the latest read went out, which spaces the reads.</param>
/// <param name="Problem">Why the latest read failed; the last report stays, as a snapshot section's does.</param>
internal sealed record CostPage(CostReport? Report, Guid? Run, int VideoScroll, bool Reading, DateTimeOffset AskedAt, string? Problem)
{
    /// <summary>The selected run's row; the newest when none is selected or the selected one is gone.</summary>
    public int Selected
    {
        get
        {
            var runs = Report?.Runs ?? [];
            for (var at = 0; at < runs.Count; at++)
            {
                if (runs[at].Id == Run) return at;
            }
            return 0;
        }
    }

    public CostRun? SelectedRun => Report is { Runs.Count: > 0 } report ? report.Runs[Selected] : null;
}

internal sealed record TuiModel(
    int Width,
    int Height,
    DateTimeOffset Now,
    string Subtitle,
    Pane Focus,
    bool Loaded,
    ImmutableList<Video> Open,
    ImmutableList<Video> Failed,
    Guid? QueueSelection,
    Guid? ReviewSelection,
    LiveRunHolder? Run,
    RunRequests? Requests,
    WorkerState Worker,
    ActivityState Activity,
    ImmutableList<LogEntry> Log,
    int LogScroll,
    TuiConfirm? Confirm,
    TuiChoice? Choice,
    bool Help,
    ImmutableDictionary<string, string> Busy,
    Notice? Notice,
    string? ReadProblem,
    bool Quit,
    FinaliseBatch? Finalising,
    CostPage? Cost = null)
{
    /// <summary>The queue pane's rows in display order: the open videos by group (a stable sort keeps run order inside one), then the failed.</summary>
    public List<Video> Ordered() => [.. Open.OrderBy(video => video.Group), .. Failed];

    public List<Video> Ready() => [.. Open.Where(video => video.Group == VideoGroup.Ready)];

    public Video? Converting => Open.FirstOrDefault(video => video.Group == VideoGroup.ConvertingNow);

    public int Count(VideoGroup group) => group == VideoGroup.Failed ? Failed.Count : Open.Count(video => video.Group == group);

    public static TuiModel Initial(DateTimeOffset now, int width, int height, string subtitle) => new(
        width, height, now, subtitle, Pane.Queue, Loaded: false,
        Open: [], Failed: [], QueueSelection: null, ReviewSelection: null,
        Run: null, Requests: null, Worker: WorkerState.Unknown, Activity: ActivityState.None,
        Log: [], LogScroll: 0, Confirm: null, Choice: null, Help: false, Busy: ImmutableDictionary<string, string>.Empty, Notice: null, ReadProblem: null, Quit: false, Finalising: null);
}

/// <summary>The worker as the pane shows it, merged from the state file and Hetzner's answer.</summary>
/// <param name="Info">Null when there is none.</param>
/// <param name="Price">EUR an hour including VAT; null while Hetzner has not said.</param>
/// <param name="NotCheckedReason">Why Hetzner wasn't asked or didn't answer; null when it answered or no answer is in yet.</param>
/// <param name="Asking">Hetzner has not answered yet.</param>
internal sealed record WorkerView(RemoteWorkerInfo? Info, decimal? Price, string? NotCheckedReason, bool Asking)
{
    /// <summary>
    /// The state file is read every refresh, so it decides whether a worker exists; Hetzner's answer adds what the file lacks
    /// (the live status and the price) when it is about the same server. A server Hetzner knows that the file doesn't (a
    /// recovered worker) shows from the answer alone.
    /// </summary>
    public static WorkerView Of(WorkerState state)
    {
        var check = state.Check;
        var answered = check is { NotCheckedReason: null };
        var info = state.Recorded ?? (answered ? check!.Worker : null);
        decimal? price = null;
        if (info is not null && answered && check!.Worker is { } live && live.ServerId == info.ServerId)
        {
            info = live;
            price = check.HourlyGross;
        }
        return new WorkerView(info, price, check?.NotCheckedReason, Asking: check is null);
    }

    public TimeSpan? Uptime(DateTimeOffset now) =>
        Info is { CreatedUtc: { } created, Status: not RemoteWorkerStatuses.PrimaryIpRetained } ? now - created : null;

    /// <summary>What the worker has billed so far, as the price an hour times its uptime; null when either is unknown.</summary>
    public decimal? Cost(DateTimeOffset now) => Price is { } price && Uptime(now) is { } up ? (decimal)up.TotalHours * price : null;
}
