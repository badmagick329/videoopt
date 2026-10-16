using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using static CliLocks;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// The console's side of what happens to the managed worker when a run ends (VOPT-54 step 3): the run lock, which starts the
/// run, and the guard that hands every way it ends to <see cref="RunFlow"/>, which decides (<see cref="WorkerEndPolicy"/>) so
/// none leaves a worker billing without a line that says so. Both also write the event file beside the console, so a TUI in
/// another window follows a foreground run.
/// </summary>
internal sealed class RunWorkerEnd(RunFlow flow, RunStart start, RunWorkerState state, RunStop stop, EventLogRunObserver eventLog)
{
    /// <summary>
    /// The run: its lock and its keep-awake hold, to dispose when the command ends. Null after saying who holds the lock. Taking
    /// it starts the run (<see cref="RunStart"/>): from then on every end decides the worker's fate (<see cref="GuardAsync"/>).
    /// </summary>
    /// <param name="holderName">How the refusal names this run to a later one, such as <c>video-optimiser run</c>.</param>
    public async Task<IDisposable?> TakeRunLockAsync(LoadedConfiguration configuration, CliCommand command, string holderName, CancellationToken cancellationToken)
    {
        var started = await start.TryStartAsync(configuration.Settings, holderName, fate: null, cancellationToken);
        if (started.Session is not null) return started.Session;
        WriteRefusal(started.Holder, command);
        return null;
    }

    /// <summary>
    /// Says that another run holds the lock. A run in a console is stopped there with Ctrl+C; a background run has no console,
    /// so its line names <c>run stop</c>.
    /// </summary>
    internal static void WriteRefusal(LiveRunHolder? holder, CliCommand command)
    {
        if (holder is null)
        {
            WriteError("Another run is starting on this database. Try again in a few seconds.");
            return;
        }
        if (holder.Command == LockHolders.BackgroundRun)
        {
            WriteError($"Another run is using this database: a background run, started {FormatStarted(holder.StartedUtc)} (process {holder.ProcessId}).");
            WriteError($"Wait for it to finish, or stop it with '{command.Hint("run stop")}'.");
            return;
        }
        WriteError($"Another run is using this database: {holder.Command}, started {FormatStarted(holder.StartedUtc)} (process {holder.ProcessId}).");
        WriteError("Wait for it to finish, or stop it with Ctrl+C in its window.");
    }

    /// <summary>
    /// Runs a run command so that every way it ends, once it holds the run lock, decides the managed worker's fate
    /// (<see cref="RunFlow.GuardAsync"/>), printing as the console front end.
    /// </summary>
    public Task<int> GuardAsync(LoadedConfiguration configuration, CliCommand command, Func<Task<int>> run) =>
        flow.GuardAsync(configuration.Settings, new TeeRunObserver(new ConsoleRunObserver(configuration, command, stop, state, beforeNext: null), eventLog), new ConsoleRunQuestions(state), run);
}

/// <summary>
/// The console's worker question for one run command, shared by the code that converts and the code that ends the run. One per
/// command, registered as a service. The flow's own state (started, used, set up, concluded) lives on <see cref="RunFlow"/>.
/// </summary>
internal sealed class RunWorkerState(IConsoleInput input)
{
    public WorkerQuestion Question { get; } = new(input);
}
