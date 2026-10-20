namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// How a stop request reaches a run with no console: reads the requests row about once a second and turns what it finds into
/// what Ctrl+C would have done to <see cref="RunStop"/> (the first request stops after this video, the "now" request stops
/// now), so the two ways of stopping cannot drift apart. It follows the row's stop after this video both ways: cleared, it
/// withdraws the stop it passed on (VOPT-187), and set again, it presses again. A stop now is passed on once and is final.
/// </summary>
/// <param name="interval">How often to read; a second by default, shorter in tests.</param>
public sealed class RunRequestPoll(IRunRequestStore requests, TimeSpan? interval = null)
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Runs until <paramref name="cancellationToken"/> is cancelled, which ends it quietly, or both stops stand passed on. A
    /// withdraw sets the first back to not passed on, so the poll keeps reading; once a stop now is in the row the row refuses a
    /// withdraw, so both passed on is final.
    /// </summary>
    public async Task PollAsync(string databasePath, RunStop stop, CancellationToken cancellationToken)
    {
        // Whether the row's stop after this video is passed on to the run's stop, so each change of the row reaches it once:
        // a second Press would be a second Ctrl+C and stop now.
        var passedOnAfterVideo = false;
        var passedOnNow = false;
        var wait = interval ?? DefaultInterval;
        try
        {
            while (!(passedOnAfterVideo && passedOnNow))
            {
                if (await TryReadAsync(databasePath, cancellationToken) is { } current)
                {
                    // In this order, so a "now" seen together with the first request is a second press, as Ctrl+C twice is.
                    if (current.StopAfterThisVideo && !passedOnAfterVideo)
                    {
                        passedOnAfterVideo = true;
                        stop.Press();
                    }
                    else if (!current.StopAfterThisVideo && passedOnAfterVideo)
                    {
                        passedOnAfterVideo = false;
                        stop.Withdraw();
                    }
                    // Once only: a stop now during the worker delete does nothing, and passing it on again after the delete
                    // would cancel what the run does next.
                    if (current.StopNow && !passedOnNow)
                    {
                        passedOnNow = true;
                        stop.StopNow();
                    }
                }
                await Task.Delay(wait, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended: that is how a poll stops.
        }
    }

    /// <summary>A failed read (a busy database) is skipped: the next read comes a second later, and a stop request stays in the row.</summary>
    private async Task<RunRequests?> TryReadAsync(string databasePath, CancellationToken cancellationToken)
    {
        try
        {
            return await requests.ReadAsync(databasePath, cancellationToken);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            return null;
        }
    }
}
