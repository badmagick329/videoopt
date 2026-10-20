using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// A run with no terminal: the hidden runner process now, the TUI's start action later. It takes the run lock as
/// <see cref="LockHolders.BackgroundRun"/>, converts the queue through <see cref="RunFlow"/>, and takes its stop requests from
/// the database (<see cref="RunRequestPoll"/>), which drive <see cref="RunStop"/> as Ctrl+C does in the foreground. It asks
/// nothing (<see cref="BackgroundRunQuestions"/>) and shows nothing: the event file is how anyone follows it.
/// </summary>
public sealed class BackgroundRun(RunFlow flow, RunStart start, RunRequestPoll poll, RunStop stop, IRunRequestStore requests, EventLogRunObserver eventLog)
{
    /// <param name="fate">What happens to the managed worker when the run ends, until someone changes it in the requests row.</param>
    /// <param name="mayCreateWorker">False when the launcher chose the worker that is up, so the run ends instead of creating one (<see cref="BackgroundRunQuestions"/>).</param>
    /// <returns>The run's exit code; <see cref="ExitCode.GeneralFailure"/> when another run holds the lock.</returns>
    public async Task<int> RunAsync(AppSettings settings, bool modeOverridden, WorkerFateChoice fate, bool mayCreateWorker)
    {
        var databasePath = settings.Database.Path;
        var observer = new TeeRunObserver(eventLog, new StopScopeRunObserver(stop));
        var questions = new BackgroundRunQuestions(requests, databasePath, fate, mayCreateWorker);
        // Held until the guard has also decided the worker's fate, not just until the conversion returns: a stop now ends the
        // conversion by an exception, and the worker is deleted after that. The next run must not start, or the machine
        // sleep, in the middle of the delete.
        IAsyncDisposable? session = null;
        try
        {
            return await flow.GuardAsync(settings, observer, questions, async () =>
            {
                var started = await start.TryStartAsync(settings, LockHolders.BackgroundRun, fate, stop.Now);
                if (started.Session is null) return (int)ExitCode.GeneralFailure;
                session = started.Session;

                using var pollEnd = CancellationTokenSource.CreateLinkedTokenSource(stop.Now);
                var polling = poll.PollAsync(databasePath, stop, pollEnd.Token);
                try
                {
                    return (int)await flow.ConvertQueueAsync(settings, modeOverridden, observer, questions, () => stop.StopsAfterThisVideo, stop.Now);
                }
                catch (RemoteWorkerCreationDeclinedException) when (!mayCreateWorker)
                {
                    // Only the launcher's choice declines here, and "No worker was created." alone doesn't say why the
                    // run did nothing. The guard records this as the run's failure in the event file.
                    throw new ChosenWorkerGoneException();
                }
                finally
                {
                    await pollEnd.CancelAsync();
                    await polling;
                }
            });
        }
        finally
        {
            if (session is not null) await session.DisposeAsync();
        }
    }
}

/// <summary>
/// A background run was launched to use the worker that was up, and by the time it needed it the worker was gone (deleted from
/// the TUI or another terminal). The run creates no server in its place: that one would bill without anyone choosing it.
/// </summary>
public sealed class ChosenWorkerGoneException() : Exception("The worker chosen for this run is gone, so no new one was created. Start the run again to choose a worker.");
