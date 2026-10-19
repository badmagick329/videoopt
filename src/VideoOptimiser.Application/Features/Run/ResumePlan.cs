using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// What processing a job does, given what an earlier run of it left: which stages run, whether the encode is a new attempt,
/// and what is reused. It reads no file and asks no worker; <see cref="JobProcessor"/> does both and passes the answers in, so
/// <see cref="JobProcessor.ProcessAsync"/> and <see cref="JobProcessor.NeedsWorkerAsync"/> decide from the same rules.
/// </summary>
/// <param name="ReusesOutput">The AV1 and its manifest on this PC are checked again; nothing is staged, encoded or fetched.</param>
/// <param name="UsesWorker">The run touches the worker for this job: the sweep before it, and the workspace cleanup after its verdict.</param>
/// <param name="ReattachesAttempt">The encode keeps the job's attempt, because the worker may hold that attempt's encode.</param>
/// <param name="AsksWorkerForFinishedEncode">Whether the worker is asked if that encode finished, after the sweep.</param>
/// <param name="FinishedOnWorker">It did: the job goes straight to the download.</param>
/// <param name="Stages">The original is uploaded to the worker.</param>
/// <param name="SearchesCrf">No CRF is known, so it is searched for.</param>
/// <param name="Encodes">An encode runs, or one still running on the worker is followed.</param>
/// <param name="Downloads">The AV1 is fetched from the worker.</param>
/// <param name="SaysEncodeFinished">The run says the encode had finished: a rerun after a stopped download watched it end, one after a stopped encode did not.</param>
internal sealed record ResumePlan(
    bool ReusesOutput,
    bool UsesWorker,
    bool ReattachesAttempt,
    bool AsksWorkerForFinishedEncode,
    bool FinishedOnWorker,
    bool Stages,
    bool SearchesCrf,
    bool Encodes,
    bool Downloads,
    bool SaysEncodeFinished)
{
    /// <summary>The encode makes a new attempt, so its files have a new name and nothing of an earlier one is picked up.</summary>
    public bool NewAttempt => !ReusesOutput && !ReattachesAttempt;

    /// <param name="job">Read for its CRF, attempt and mode, after the stopped job was requeued.</param>
    /// <param name="resumeStatus">Where the job stopped, as read before it was requeued.</param>
    /// <param name="originalChanged">The original differs from the one the job last read (VOPT-138). Nothing made of the
    /// old one is reused: the plan is a fresh start, a new attempt with a new search.</param>
    /// <param name="sessionIsRemote">Whether this run's session is on a worker.</param>
    /// <param name="outputOnDisk">The job's AV1 and its manifest are both on this PC.</param>
    /// <param name="finishedOnWorker">The worker's answer, once <see cref="AsksWorkerForFinishedEncode"/> asked it; an answer
    /// to a question the plan did not ask changes nothing.</param>
    public static ResumePlan Plan(JobRecord job, JobStatus? resumeStatus, bool originalChanged, bool sessionIsRemote, bool outputOnDisk, bool finishedOnWorker = false)
    {
        var resume = originalChanged ? null : resumeStatus;
        var crfKnown = !originalChanged && job.Crf is not null;
        var reusesOutput = CanReuseOutput(resume, outputOnDisk);
        var reattaches = MayHaveEncodeOnWorker(job, resume);
        var asks = !reusesOutput && reattaches && crfKnown;
        var finished = asks && finishedOnWorker;
        return new ResumePlan(
            ReusesOutput: reusesOutput,
            UsesWorker: sessionIsRemote && !reusesOutput,
            ReattachesAttempt: reattaches,
            AsksWorkerForFinishedEncode: asks,
            FinishedOnWorker: finished,
            // Staging hashes the original and needs a worker, and a resume that only has to download or check what is
            // already finished needs neither (VOPT-72, VOPT-89).
            Stages: sessionIsRemote && !reusesOutput && !finished,
            SearchesCrf: !crfKnown,
            Encodes: !reusesOutput && !finished,
            Downloads: sessionIsRemote && !reusesOutput,
            SaysEncodeFinished: finished && resume == JobStatus.Encoding);
    }

    /// <summary>A job stopped while its AV1 was being checked, with that AV1 and its manifest still on this PC: nothing is left to encode or fetch.</summary>
    public static bool CanReuseOutput(JobStatus? resumeStatus, bool outputOnDisk) => resumeStatus == JobStatus.Validating && outputOnDisk;

    /// <summary>
    /// A remote job stopped in or after an encode it started: the worker may hold that encode, finished or still running, which
    /// a rerun reattaches to rather than paying for again. Read from the job's own mode, because the exists check asks before
    /// a session is open; past the compatibility check a started job's mode is the session's.
    /// </summary>
    public static bool MayHaveEncodeOnWorker(JobRecord job, JobStatus? resumeStatus) =>
        string.Equals(job.ExecutionMode, ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase)
        && resumeStatus is JobStatus.Encoding or JobStatus.Downloading && job.Attempt > 0;
}
