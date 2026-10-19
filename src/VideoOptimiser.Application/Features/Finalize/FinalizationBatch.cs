using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Finalize;

/// <summary>
/// What a finalise batch reports as it goes: facts, never wording. <c>finalize --ready</c> and <c>immich finalize</c> print
/// them; the TUI posts them to its loop, so Review shows the batch's progress.
/// </summary>
public interface IFinalizationBatchObserver
{
    /// <summary>The video is about to be finalised. <paramref name="job"/> is as read just before; null when it is gone.</summary>
    void VideoStarted(Guid id, JobRecord? job);

    /// <summary>The video's finalisation ended, finalised or not. Every started video ends, and so does one whose read failed.</summary>
    void VideoEnded(FinalizationBatchVideo video);
}

/// <summary>How one video of a batch ended.</summary>
/// <param name="Job">The job as read before its finalisation, for its name and sizes; null when it is gone or the read failed.</param>
/// <param name="Result">The finalisation's own answer; null when it threw.</param>
/// <param name="Error">What was thrown instead of an answer, such as a failed database read.</param>
public sealed record FinalizationBatchVideo(Guid Id, JobRecord? Job, FinalizationResult? Result, Exception? Error)
{
    public bool Succeeded => Result?.ExitCode == ExitCode.Success;
}

/// <summary>How a finalise batch ended; the front end words it.</summary>
public abstract record FinalizationBatchOutcome;

/// <summary>Another finalisation holds the finalise lock, so nothing was finalised.</summary>
/// <param name="Holder">Who holds it; null while it was still writing its details.</param>
public sealed record FinalizationBatchRefused(FinalizeLockHolder? Holder) : FinalizationBatchOutcome;

/// <summary>The batch's tally. <see cref="Left"/> is above 0 only when a stop between videos ended it early.</summary>
public sealed record FinalizationBatchTally(int Total, int Finalised, int Failed) : FinalizationBatchOutcome
{
    public int Left => Total - Finalised - Failed;
}

/// <summary>
/// Finalises a list of videos one after another under the finalise lock: the loop <c>finalize --ready</c>,
/// <c>immich finalize</c> and the TUI's <c>f</c> and <c>F</c> share. A failed video, thrown or answered, counts as failed and
/// the next one still goes, since each video's finalisation stands alone. Only <paramref name="cancellationToken"/> (Ctrl+C,
/// the TUI closing) cuts a video off; it then throws, so a CLI exits cancelled whatever the counts.
/// </summary>
public sealed class FinalizationBatch(IJobRepository jobs, IFinalizationService finalizer, IDatabaseLocks locks)
{
    /// <summary>
    /// Takes the finalise lock as <paramref name="command"/>, so a run leaves these videos alone while they are finalised, then
    /// runs the batch. Refused, finalising nothing, while another finalisation holds it.
    /// </summary>
    public async Task<FinalizationBatchOutcome> RunAsync(IReadOnlyList<Guid> ids, AppSettings settings, string command, IFinalizationBatchObserver observer, CancellationToken stopAfterVideo, CancellationToken cancellationToken)
    {
        var attempt = await locks.TakeFinalizeAsync(settings.Database.Path, command, cancellationToken);
        if (attempt.Lock is not { } held) return new FinalizationBatchRefused(attempt.Holder);
        using (held) return await RunHeldAsync(ids, settings, observer, stopAfterVideo, cancellationToken);
    }

    /// <summary>
    /// The batch for a caller that already holds the finalise lock: the CLI takes it before it lists the videos, so the list
    /// it asks about is the one finalised.
    /// </summary>
    /// <param name="stopAfterVideo">
    /// A stop that lets the current video finish: read only between videos and never handed to the finalisation, so a video
    /// once started always finishes, and the ones after it stay ready.
    /// </param>
    public async Task<FinalizationBatchTally> RunHeldAsync(IReadOnlyList<Guid> ids, AppSettings settings, IFinalizationBatchObserver observer, CancellationToken stopAfterVideo, CancellationToken cancellationToken)
    {
        var database = settings.Database.Path;
        var finalised = 0;
        var failed = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopAfterVideo.IsCancellationRequested) break;
            var video = await FinalizeOneAsync(database, id, settings, observer, cancellationToken);
            if (video.Succeeded) finalised++;
            else failed++;
            observer.VideoEnded(video);
        }

        return new FinalizationBatchTally(ids.Count, finalised, failed);
    }

    private async Task<FinalizationBatchVideo> FinalizeOneAsync(string database, Guid id, AppSettings settings, IFinalizationBatchObserver observer, CancellationToken cancellationToken)
    {
        JobRecord? job;
        try
        {
            job = await jobs.GetAsync(database, id, cancellationToken);
        }
        catch (Exception exception) when (!IsClosing(exception, cancellationToken))
        {
            return new FinalizationBatchVideo(id, null, null, exception);
        }

        // Outside the catches: an observer's own failure is not the video's.
        observer.VideoStarted(id, job);
        try
        {
            return new FinalizationBatchVideo(id, job, await finalizer.FinalizeAsync(database, id, settings, cancellationToken), null);
        }
        catch (Exception exception) when (!IsClosing(exception, cancellationToken))
        {
            return new FinalizationBatchVideo(id, job, null, exception);
        }
    }

    /// <summary>
    /// Only the caller's own token ends the batch. Any other cancellation, such as an <c>HttpClient</c> timeout inside an Immich
    /// finalisation, is that video's failure (VOPT-142).
    /// </summary>
    private static bool IsClosing(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
}
