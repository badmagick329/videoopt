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

public enum WithdrawStopOutcome
{
    NoRun,
    /// <summary>A run in a console holds the lock; it never asked anything of the requests row, so there is nothing to withdraw.</summary>
    Foreground,
    Withdrawn,
    /// <summary>The background run has no stop after this video asked.</summary>
    NoStopAsked,
    /// <summary>A stop now is asked, which can't be withdrawn.</summary>
    StoppingNow
}

/// <param name="Holder">The run found; null for <see cref="WithdrawStopOutcome.NoRun"/>.</param>
public sealed record WithdrawStopResult(WithdrawStopOutcome Outcome, LiveRunHolder? Holder);

/// <summary>
/// <c>run stop</c>: asks the background run going on this database to stop, through the requests row, or withdraws a stop
/// after this video asked of it (VOPT-187). The CLI and the TUI both come through here, never through <see cref="RunStop"/>,
/// which belongs to the run's own process.
/// </summary>
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

    /// <remarks>
    /// Withdrawn means the row is cleared, not that the run carries on: once the run has read the stop between videos it ends
    /// anyway. The user's decision: a late withdraw is accepted, not reported, so no handshake runs between the processes.
    /// </remarks>
    public async Task<WithdrawStopResult> WithdrawAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (locks.LiveRun(databasePath) is not { } holder) return new WithdrawStopResult(WithdrawStopOutcome.NoRun, null);
        if (!holder.IsBackground) return new WithdrawStopResult(WithdrawStopOutcome.Foreground, holder);
        if (await requests.WithdrawStopAsync(databasePath, cancellationToken)) return new WithdrawStopResult(WithdrawStopOutcome.Withdrawn, holder);
        // Read after the refusal, so the outcome says why it was refused as the row stands now.
        var current = await requests.ReadAsync(databasePath, cancellationToken);
        return new WithdrawStopResult(current.StopNow ? WithdrawStopOutcome.StoppingNow : WithdrawStopOutcome.NoStopAsked, holder);
    }
}
