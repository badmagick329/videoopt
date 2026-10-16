using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Infrastructure.Persistence;
using static CliLocks;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// The console's side of what happens to the managed worker when a run ends (VOPT-54 step 3): the run lock, which starts the
/// run, and the guard that hands every way it ends to <see cref="RunFlow"/>, which decides (<see cref="WorkerEndPolicy"/>) so
/// none leaves a worker billing without a line that says so.
/// </summary>
internal sealed class RunWorkerEnd(RunFlow flow, RunWorkerState state, RunStop stop)
{
    /// <summary>The run lock, or null after saying who holds it. Taking it starts the run: from then on every end decides the worker's fate (<see cref="GuardAsync"/>).</summary>
    /// <param name="command">How the refusal names this run to a later one, such as <c>video-optimiser run</c>.</param>
    public DatabaseLock? TakeRunLock(LoadedConfiguration configuration, string command)
    {
        var taken = DatabaseLock.TryAcquire(DatabaseLock.RunPath(configuration.Settings.Database.Path), command, out var holder);
        if (taken is not null)
        {
            flow.MarkStarted();
            return taken;
        }
        if (holder is null)
        {
            WriteError("Another run is starting on this database. Try again in a few seconds.");
            return null;
        }
        WriteError($"Another run is using this database: {holder.Command}, started {FormatStarted(holder.StartedUtc)} (process {holder.ProcessId}).");
        WriteError("Wait for it to finish, or stop it with Ctrl+C in its window.");
        return null;
    }

    /// <summary>
    /// Runs a run command so that every way it ends, once it holds the run lock, decides the managed worker's fate
    /// (<see cref="RunFlow.GuardAsync"/>), printing as the console front end.
    /// </summary>
    public Task<int> GuardAsync(LoadedConfiguration configuration, CliCommand command, Func<Task<int>> run) =>
        flow.GuardAsync(configuration.Settings, new ConsoleRunObserver(configuration, command, stop, state, beforeNext: null), new ConsoleRunQuestions(state), run);
}

/// <summary>
/// The console's worker question for one run command, shared by the code that converts and the code that ends the run. One per
/// command, registered as a service. The flow's own state (started, used, set up, concluded) lives on <see cref="RunFlow"/>.
/// </summary>
internal sealed class RunWorkerState(IConsoleInput input)
{
    public WorkerQuestion Question { get; } = new(input);
}
