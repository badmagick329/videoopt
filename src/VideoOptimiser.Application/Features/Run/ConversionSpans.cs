using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// One run's conversion spans: the time each processing attempt of a video took, on this PC or the worker alike, so the cost of
/// a video's freed space is its own time on a managed worker (VOPT-126). <see cref="RunFlow"/> makes one per conversion, once
/// the run's worker is known, and <see cref="QueueService"/> opens and closes a span around each attempt.
/// </summary>
/// <param name="workerServerId">The run's managed worker as the conversion starts; null on this PC and on a manual SSH host.</param>
public sealed class ConversionSpans(IRunRecordStore records, TimeProvider time, string databasePath, Guid runId, long? workerServerId)
{
    public Task<long> OpenAsync(Guid jobId, CancellationToken cancellationToken) =>
        records.OpenSpanAsync(databasePath, runId, jobId, workerServerId, time.GetUtcNow(), cancellationToken);

    /// <summary>
    /// Takes no token: after a stop the run's token is already cancelled, and the stopped attempt's time still counts. A close
    /// that fails is dropped, since it runs while the attempt's own outcome or exception leaves, and losing an end time must not
    /// change how the run ends; the span then reads like one a killed run left open.
    /// </summary>
    public async Task CloseAsync(long spanId)
    {
        try
        {
            await records.CloseSpanAsync(databasePath, spanId, time.GetUtcNow(), CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }
}
