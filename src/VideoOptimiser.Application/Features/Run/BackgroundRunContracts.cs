using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// What a background run does with the managed worker when it ends, chosen before it starts and changeable while it goes
/// (phase C: a background run never waits on a question, so nothing bills while waiting for an answer).
/// </summary>
public enum WorkerFateChoice
{
    Delete,
    Keep
}

/// <summary>What other processes have asked of the run going on a database.</summary>
/// <param name="WorkerFate">The background run's fate for the worker; null for a foreground run, whose fate stays with its console.</param>
public sealed record RunRequests(bool StopAfterThisVideo, bool StopNow, WorkerFateChoice? WorkerFate);

/// <summary>
/// The requests row in the database (migration 10): how <c>run stop</c> and, later, the TUI reach a run that has no terminal.
/// One row for the run going now; the run polls it about once a second.
/// </summary>
public interface IRunRequestStore
{
    /// <summary>
    /// Called by a run once it holds the run lock: drops every request an earlier run left and records <paramref name="fate"/>,
    /// so a stale stop never stops the new run.
    /// </summary>
    Task ResetAsync(string databasePath, WorkerFateChoice? fate, CancellationToken cancellationToken);

    /// <summary>The requests as they stand; nothing requested when no run has reset them yet.</summary>
    Task<RunRequests> ReadAsync(string databasePath, CancellationToken cancellationToken);

    /// <param name="now">True for a stop now (<c>run stop --now</c>), false for a stop after this video.</param>
    Task RequestStopAsync(string databasePath, bool now, CancellationToken cancellationToken);

    Task SetWorkerFateAsync(string databasePath, WorkerFateChoice fate, CancellationToken cancellationToken);
}

/// <summary>What a run's event says happened. Facts only: a reader words them.</summary>
public enum RunEventKind
{
    /// <summary>The run took the run lock. <see cref="RunEvent.Text"/> is its lock holder name, <see cref="RunEvent.Count"/> its process ID, <see cref="RunEvent.Fate"/> a background run's worker fate.</summary>
    RunStarted,
    VideoSkipped,
    FinalisationInterrupted,
    VideosHeld,
    ImmichRestaging,
    ImmichVideosStayQueued,
    WorkerProgress,
    ConversionStarted,
    /// <summary>One <see cref="JobProgress"/> from the video converting now.</summary>
    Progress,
    ConversionEnded,
    JoiningVideoSkipped,
    JoiningVideoWaitsForWorker,
    StoppedByUnreachableWorker,
    StoppedAfter,
    Summary,
    StoppedWithoutManagedWorker,
    StoppedNow,
    WorkerUnreachableAtEnd,
    WorkerKept,
    WorkerDeleting,
    WorkerDeleteFailed,
    WorkerDeleteEnded,
    ManualWorkerUnreachable,
    ManualWorkerReminder,
    RunFailed
}

/// <summary>
/// One line of the event file: one <see cref="IRunObserver"/> event, or the run's start. Flat, with only the fields its
/// <see cref="Kind"/> needs set, so the file stays one JSON object per line through the AOT JSON context.
/// </summary>
/// <param name="JobId">The video the event is about.</param>
/// <param name="SourcePath">That video's original.</param>
/// <param name="Count">The event's count: videos held, staying queued, converting, not started, or ready.</param>
/// <param name="Text">Free text the event carries: a worker progress line, a progress line, an error, the reason Immich can't be used.</param>
/// <param name="Worker">The managed worker's name, or the manual SSH host.</param>
/// <param name="Fate">The worker's fate, as <see cref="WorkerFate"/> or <see cref="WorkerFateChoice"/> names it.</param>
/// <param name="Stage">A job status: the stage a stopped video was in.</param>
/// <param name="Progress">For <see cref="RunEventKind.Progress"/>: the <see cref="JobProgressKind"/>.</param>
/// <param name="Tone">For <see cref="RunEventKind.Progress"/>: the <see cref="JobTone"/>.</param>
/// <param name="Flag">The event's yes/no: worker up at conversion start, stopped after a video, question follows, answered no, setting up the worker.</param>
/// <param name="Failures">For <see cref="RunEventKind.Summary"/>: the failed videos.</param>
/// <param name="Percent">For <see cref="RunEventKind.Progress"/>: <see cref="JobProgress.Percent"/>, on a reading the next one replaces.</param>
/// <param name="ToCheck">For <see cref="RunEventKind.Summary"/>: of <paramref name="Ready"/>, the videos with validation warnings.</param>
public sealed record RunEvent(
    DateTimeOffset At,
    RunEventKind Kind,
    Guid? JobId = null,
    string? SourcePath = null,
    int? Count = null,
    int? Ready = null,
    int? Skipped = null,
    string? Text = null,
    string? Worker = null,
    string? Fate = null,
    string? Stage = null,
    string? RunEnd = null,
    string? Progress = null,
    string? Tone = null,
    bool? Flag = null,
    IReadOnlyList<FailedQueueJob>? Failures = null,
    int? Percent = null,
    int? ToCheck = null);

/// <summary>
/// The event file beside the database (<c>&lt;db&gt;.run.events</c>), JSON lines: a TUI open in another window follows a run
/// live, and a reopened one catches up by reading it from the start. One file; the next run replaces it.
/// </summary>
public interface IRunEventLog
{
    /// <summary>Called once the run holds the run lock: replaces the previous run's file. Appends before this are dropped.</summary>
    void Begin(string databasePath);

    /// <summary>Appends one line and flushes it, so a reader sees it at once.</summary>
    void Append(RunEvent runEvent);

    /// <summary>Every event in the file, in order; empty when there is none. A torn last line (a write in progress) is left out.</summary>
    IReadOnlyList<RunEvent> Read(string databasePath);
}

/// <summary>
/// Keeps Windows from sleeping while a run works. Every run that takes the run lock holds it, foreground CLI runs included
/// (the user's decision, 2026-10-16).
/// </summary>
public interface IKeepAwake
{
    /// <summary>Held until the result is disposed.</summary>
    IDisposable Hold();
}

/// <summary>A process started detached from the console: no console, no inherited handles, survives its terminal closing.</summary>
public interface IDetachedProcess : IDisposable
{
    int ProcessId { get; }

    /// <summary>The exit code once it has exited, or null if it is still running after <paramref name="timeout"/>.</summary>
    int? WaitForExit(TimeSpan timeout);
}

/// <summary>Starts this exe again, detached (Trial B: <c>DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP</c>, no handles inherited).</summary>
public interface IDetachedLauncher
{
    IDetachedProcess Launch(IReadOnlyList<string> arguments, string workingDirectory);
}
