using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>The outcome of <see cref="RunStart.TryStartAsync"/>.</summary>
/// <param name="Session">The run, while it lasts: dispose it when the run ends. Null when another run holds the lock.</param>
/// <param name="Holder">Who holds the run lock when the start was refused; null while that holder is still writing its details.</param>
public sealed record RunStartResult(IDisposable? Session, LiveRunHolder? Holder)
{
    public bool Started => Session is not null;
}

/// <summary>
/// How every run begins once it has its settings: the CLI's <c>run</c> and <c>immich run</c>, the background runner and later
/// the TUI. One place, so each takes the run lock, clears the previous run's requests and event file, and keeps Windows awake
/// alike, and a front end cannot forget one.
/// </summary>
public sealed class RunStart(IDatabaseLocks locks, IRunRequestStore requests, IRunEventLog events, IKeepAwake keepAwake, RunFlow flow, TimeProvider time)
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
            flow.MarkStarted();
            // Replaces the earlier run's file and clears its requests only now, with the lock held: before this, an earlier run
            // may still be writing both.
            events.Begin(databasePath);
            events.Append(new RunEvent(time.GetUtcNow(), RunEventKind.RunStarted, Text: holderName, Count: Environment.ProcessId, Fate: fate?.ToString()));
            await requests.ResetAsync(databasePath, fate, cancellationToken);
            awake = keepAwake.Hold();
            return new RunStartResult(new Session(awake, runLock), null);
        }
        catch
        {
            awake?.Dispose();
            runLock.Dispose();
            throw;
        }
    }

    /// <summary>Releases keep-awake first, then the lock: once another run can start, this one no longer holds the machine awake.</summary>
    private sealed class Session(IDisposable keepAwake, IDisposable runLock) : IDisposable
    {
        public void Dispose()
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
