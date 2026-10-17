using VideoOptimiser.Application.Configuration;
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
    /// <returns>The run's exit code; <see cref="ExitCode.GeneralFailure"/> when another run holds the lock.</returns>
    public async Task<int> RunAsync(AppSettings settings, bool modeOverridden, WorkerFateChoice fate)
    {
        var databasePath = settings.Database.Path;
        var observer = new TeeRunObserver(eventLog, new StopScopeRunObserver(stop));
        var questions = new BackgroundRunQuestions(requests, databasePath, fate);
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
                    return (int)await flow.ConvertQueueAsync(settings, modeOverridden, observer, questions, stop.AfterThisVideo, stop.Now);
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
