using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Application.Features.Run;

public enum BackgroundLaunchOutcome
{
    /// <summary>A run already holds the lock; nothing was launched.</summary>
    AlreadyRunning,
    /// <summary>The runner holds the run lock as a background run.</summary>
    Started,
    /// <summary>The runner ended before it took the lock.</summary>
    ExitedEarly,
    /// <summary>The runner is alive but has not taken the lock in time.</summary>
    NotConfirmed
}

/// <param name="Holder">For <see cref="BackgroundLaunchOutcome.AlreadyRunning"/>, the run found.</param>
/// <param name="ProcessId">The runner's process ID, when one was launched.</param>
/// <param name="ExitCode">For <see cref="BackgroundLaunchOutcome.ExitedEarly"/>, the runner's exit code.</param>
public sealed record BackgroundLaunchResult(BackgroundLaunchOutcome Outcome, LiveRunHolder? Holder = null, int? ProcessId = null, int? ExitCode = null);

/// <summary>
/// Starts a background run: launches the runner detached and waits until the run lock shows that very process as a
/// background run, so "started" means the run exists and not that a process was created. The CLI's hidden launcher uses it
/// now, and the TUI's start action later.
/// </summary>
/// <param name="confirmWithin">How long to wait for the runner to take the lock; 15 seconds by default (config load, database migration and the lock itself).</param>
/// <param name="pollEvery">How often to look; a tenth of a second by default.</param>
public sealed class BackgroundLaunch(IDatabaseLocks locks, IDetachedLauncher launcher, TimeProvider time, TimeSpan? confirmWithin = null, TimeSpan? pollEvery = null)
{
    private static readonly TimeSpan DefaultConfirmWithin = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultPollEvery = TimeSpan.FromMilliseconds(100);

    /// <param name="arguments">The runner's command line, after the exe.</param>
    public async Task<BackgroundLaunchResult> StartAsync(string databasePath, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        if (locks.LiveRun(databasePath) is { } running) return new BackgroundLaunchResult(BackgroundLaunchOutcome.AlreadyRunning, running);

        using var process = launcher.Launch(arguments, workingDirectory);
        var deadline = time.GetUtcNow() + (confirmWithin ?? DefaultConfirmWithin);
        while (true)
        {
            // The lock first: a runner that took it and then died shows as exited, not as started.
            if (locks.LiveRun(databasePath) is { IsBackground: true } holder && holder.ProcessId == process.ProcessId)
            {
                return new BackgroundLaunchResult(BackgroundLaunchOutcome.Started, holder, process.ProcessId);
            }
            if (process.WaitForExit(TimeSpan.Zero) is { } exitCode) return new BackgroundLaunchResult(BackgroundLaunchOutcome.ExitedEarly, ProcessId: process.ProcessId, ExitCode: exitCode);
            if (time.GetUtcNow() >= deadline) return new BackgroundLaunchResult(BackgroundLaunchOutcome.NotConfirmed, ProcessId: process.ProcessId);
            await Task.Delay(pollEvery ?? DefaultPollEvery, time, cancellationToken);
        }
    }
}
