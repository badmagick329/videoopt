using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>The outcome of <see cref="RunStart.TryStartAsync"/>.</summary>
/// <param name="Session">The run, while it lasts: dispose it when the run ends. Null when another run holds the lock.</param>
/// <param name="Holder">Who holds the run lock when the start was refused; null while that holder is still writing its details.</param>
public sealed record RunStartResult(IAsyncDisposable? Session, LiveRunHolder? Holder)
{
    public bool Started => Session is not null;
}

/// <summary>
/// How every run begins once it has its settings: the CLI's <c>run</c> and <c>immich run</c>, the background runner and later
/// the TUI. One place, so each takes the run lock, clears the previous run's requests and event file, records the run
/// (<see cref="IRunRecordStore"/>) and keeps Windows awake alike, and a front end cannot forget one.
/// </summary>
public sealed class RunStart(IDatabaseLocks locks, IRunRequestStore requests, IRunEventLog events, IKeepAwake keepAwake, IRunRecordStore records, RunFlow flow, TimeProvider time)
{
    /// <param name="holderName">How the run names itself in the lock, such as <see cref="LockHolders.BackgroundRun"/>.</param>
    /// <param name="fate">A background run's worker fate, recorded in the requests row; null for a foreground run, whose fate stays with its console.</param>
    public async Task<RunStartResult> TryStartAsync(AppSettings settings, string holderName, WorkerFateChoice? fate, CancellationToken cancellationToken)
    {
        var databasePath = settings.Database.Path;
        var runLock = locks.TakeRun(databasePath, holderName, out var holder);
        if (runLock is null) return new RunStartResult(null, holder);

        IDisposable? awake = null;
        try
        {
            var runId = Guid.NewGuid();
            flow.MarkLockTaken();
            // Replaces the earlier run's file and clears its requests only now, with the lock held: before this, an earlier run
            // may still be writing both.
            events.Begin(databasePath);
            events.Append(new RunEvent(time.GetUtcNow(), RunEventKind.RunStarted, Text: holderName, Count: Environment.ProcessId, Fate: fate?.ToString()));
            await requests.ResetAsync(databasePath, fate, cancellationToken);
            // Last before keep-awake, so a run whose start failed leaves no row.
            await records.AddRunAsync(databasePath, runId, holderName, time.GetUtcNow(), cancellationToken);
            awake = keepAwake.Hold();
            // Last, so a start that failed above (and released the lock) never decides a worker's fate (VOPT-156).
            flow.MarkStarted(runId);
            return new RunStartResult(new Session(records, time, databasePath, runId, awake, runLock), null);
        }
        catch
        {
            awake?.Dispose();
            runLock.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Ends the run's row while it still holds the lock, so the next run starts after this one ended; then releases keep-awake,
    /// then the lock: once another run can start, this one no longer holds the machine awake. The end takes no token, since
    /// a stopped run's is cancelled, and a failed write still releases both. A run killed before this keeps no end time.
    /// </summary>
    private sealed class Session(IRunRecordStore records, TimeProvider time, string databasePath, Guid runId, IDisposable keepAwake, IDisposable runLock) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await records.EndRunAsync(databasePath, runId, time.GetUtcNow(), CancellationToken.None);
            }
            finally
            {
                try
                {
                    keepAwake.Dispose();
                }
                finally
                {
                    runLock.Dispose();
                }
            }
        }
    }
}
