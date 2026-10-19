using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Application.Features.Run;

public enum StopRunOutcome
{
    NoRun,
    /// <summary>A run in a console holds the lock; its stopping stays with Ctrl+C in that window.</summary>
    Foreground,
    Requested
}

/// <param name="Holder">The run found; null for <see cref="StopRunOutcome.NoRun"/>.</param>
public sealed record StopRunResult(StopRunOutcome Outcome, LiveRunHolder? Holder);

/// <summary><c>run stop</c>: asks the background run going on this database to stop, through the requests row.</summary>
public sealed class StopRunRequest(IDatabaseLocks locks, IRunRequestStore requests)
{
    /// <param name="now">True for a stop now (<c>--now</c>), false for a stop after this video.</param>
    public async Task<StopRunResult> StopAsync(string databasePath, bool now, CancellationToken cancellationToken)
    {
        if (locks.LiveRun(databasePath) is not { } holder) return new StopRunResult(StopRunOutcome.NoRun, null);
        // A foreground run never reads the requests row, so a request written for it would be silently ignored.
        if (!holder.IsBackground) return new StopRunResult(StopRunOutcome.Foreground, holder);
        await requests.RequestStopAsync(databasePath, now, cancellationToken);
        return new StopRunResult(StopRunOutcome.Requested, holder);
    }
}
