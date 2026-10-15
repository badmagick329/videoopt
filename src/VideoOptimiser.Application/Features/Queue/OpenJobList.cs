using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Queue;

/// <summary>The part of <c>queue list</c> and <c>status</c> a job is shown under.</summary>
public enum OpenJobGroup
{
    ConvertingNow,
    /// <summary>Waiting and stopped videos a run takes, in run order.</summary>
    Queue,
    Held,
    /// <summary>Ready, last finalise stopped, being finalised or stopped while it was: finalize's work, not a run's.</summary>
    ReadyToFinalise
}

/// <summary>The step a stopped job picks up from. A step its place cannot have (an upload for a local job) is <see cref="Beginning"/>.</summary>
public enum ResumeStep { Beginning, Upload, CrfSearch, Encode, Download, Check }

/// <summary>Where a stopped job ran, which decides whether work left on a worker can be picked up, or only started again on a new one.</summary>
public enum ResumeWhere { Local, ManualWorker, ManagedWorker }

/// <param name="WorkerUnreachable">The worker stopped answering, which is why it stopped (<see cref="FailureCategories.RemoteUnavailable"/>).</param>
public sealed record StoppedJob(ResumeStep Step, ResumeWhere Where, bool WorkerUnreachable);

/// <param name="State">What the job is doing now (<see cref="OpenJobState"/>), for the ready group's notes.</param>
/// <param name="Stopped">How a queued or held job that is stopped picks up; null for any other.</param>
public sealed record OpenJobEntry(JobRecord Job, OpenJobGroup Group, OpenJobState State, StoppedJob? Stopped);

public static class OpenJobList
{
    /// <summary>The open jobs in run order, each with its group; the groups keep that order.</summary>
    public static IReadOnlyList<OpenJobEntry> Group(IEnumerable<JobRecord> open, bool runGoing, bool finaliseGoing) =>
        JobRunOrder.Sort(open).Select(job => Entry(job, runGoing, finaliseGoing)).ToArray();

    private static OpenJobEntry Entry(JobRecord job, bool runGoing, bool finaliseGoing)
    {
        // The same classifier as queue add: a converting status with no run going is a stop a crash left.
        var state = QueueDiscovery.OpenStateOf(job, runGoing, finaliseGoing);
        var group = state switch
        {
            OpenJobState.Held => OpenJobGroup.Held,
            OpenJobState.LastFinaliseFailed or OpenJobState.ReadyToFinalize or OpenJobState.StoppedWhileFinalising or OpenJobState.Finalising => OpenJobGroup.ReadyToFinalise,
            _ => job.IsConverting && runGoing ? OpenJobGroup.ConvertingNow : OpenJobGroup.Queue
        };
        var stopped = group is OpenJobGroup.Queue or OpenJobGroup.Held && (job.Status == JobStatus.Interrupted || job.IsConverting && !runGoing);
        return new OpenJobEntry(job, group, state, stopped ? StoppedAt(job) : null);
    }

    private static StoppedJob StoppedAt(JobRecord job)
    {
        var stage = job.IsConverting ? job.Status : job.ResumeStatus;
        var where = !string.Equals(job.ExecutionMode, ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase) ? ResumeWhere.Local
            : RemoteExecutionIdentity.IsManagedHetzner(job.RemoteHost) ? ResumeWhere.ManagedWorker
            : ResumeWhere.ManualWorker;
        var remote = where != ResumeWhere.Local;
        var step = stage switch
        {
            JobStatus.Staging when remote => ResumeStep.Upload,
            JobStatus.CrfSearching => ResumeStep.CrfSearch,
            JobStatus.Encoding => ResumeStep.Encode,
            JobStatus.Downloading when remote => ResumeStep.Download,
            JobStatus.Validating => ResumeStep.Check,
            _ => ResumeStep.Beginning
        };
        return new StoppedJob(step, where, job is { Status: JobStatus.Interrupted, FailureCategory: FailureCategories.RemoteUnavailable });
    }
}
