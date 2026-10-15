namespace VideoOptimiser.Application.Features.Run;

/// <summary>How a run ended, for <see cref="WorkerEndPolicy"/>.</summary>
public enum RunEnd
{
    /// <summary>Every video was tried, failed ones included, or there was nothing to convert.</summary>
    Finished,
    /// <summary>Ctrl+C once: the run stopped after a video, with videos left to start.</summary>
    StoppedAfterVideo,
    /// <summary>Ctrl+C twice, or once with no video converting.</summary>
    StoppedNow,
    /// <summary>The worker stopped answering; the video resumes from its stage.</summary>
    WorkerUnreachable,
    /// <summary>An error ended the run.</summary>
    Failed
}

public enum WorkerFate
{
    Delete,
    /// <summary>Kept, with lines that say it bills and how to delete it.</summary>
    Keep,
    /// <summary>Ask now: "Delete the worker now? [y/N]".</summary>
    Ask
}

/// <summary>
/// What happens to a managed worker when a run ends (VOPT-54 step 3). Every end decides, so no end leaves a worker billing
/// without a line saying so. A worker cannot delete itself (the Hetzner token never leaves this PC), so this is the only place
/// billing stops on its own.
/// </summary>
public static class WorkerEndPolicy
{
    /// <param name="answer">The answer to the question at the first Ctrl+C: true for y, false for N, null when it was not asked or got no answer.</param>
    /// <param name="workerUsed">Whether this run used the worker. A worker kept from an earlier run that this one never needed is the user's to delete.</param>
    /// <param name="deleteLosesWork">A stop now while a CRF search, an encode or a finished encode exists only on the worker.</param>
    /// <param name="canAsk">Input is interactive. Redirected input never answers the worker question: a piped "y" is for the creation prompt.</param>
    public static WorkerFate Decide(RunEnd end, bool? answer, bool deleteAfterRun, bool workerUsed, bool deleteLosesWork, bool canAsk)
    {
        // A y given before the second press did not know the work it would throw away, so that case is asked again.
        if (answer == true) return end == RunEnd.StoppedNow && deleteLosesWork && canAsk ? WorkerFate.Ask : WorkerFate.Delete;
        if (answer == false) return WorkerFate.Keep;
        return end switch
        {
            // Failed videos no longer keep the worker: a retry is a new job that starts again from the upload, so nothing a failed
            // video left on the worker is ever reused.
            RunEnd.Finished => workerUsed && deleteAfterRun ? WorkerFate.Delete : WorkerFate.Keep,
            RunEnd.StoppedAfterVideo or RunEnd.StoppedNow => canAsk ? WorkerFate.Ask : WorkerFate.Keep,
            _ => WorkerFate.Keep
        };
    }

    public static bool IsAffirmative(string? response) =>
        string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(response?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
}
