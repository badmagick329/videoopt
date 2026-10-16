namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// How a stop request reaches a run with no console: reads the requests row about once a second and turns what it finds into
/// what Ctrl+C would have done to <see cref="RunStop"/> (the first request stops after this video, the "now" request stops
/// now), so the two ways of stopping cannot drift apart.
/// </summary>
/// <param name="interval">How often to read; a second by default, shorter in tests.</param>
public sealed class RunRequestPoll(IRunRequestStore requests, TimeSpan? interval = null)
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    /// <summary>Runs until <paramref name="cancellationToken"/> is cancelled, which ends it quietly, or both stops have been passed on.</summary>
    public async Task PollAsync(string databasePath, RunStop stop, CancellationToken cancellationToken)
    {
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
