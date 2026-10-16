using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Queue;

/// <summary>
/// What a queue verb did to a job, or why it did not. Each outcome carries the facts the CLI needs to word it and nothing
/// else; the job is the one as the verb last read it (after the re-read that follows a stale converting status). A verb that
/// loses a race to another command does not return an outcome: it throws <see cref="JobChangedException"/>.
/// </summary>
public abstract record QueueVerbOutcome(JobRecord Job);

/// <summary><c>queue hold</c> parked the job.</summary>
public sealed record JobHeld(JobRecord Job) : QueueVerbOutcome(Job);

public sealed record JobAlreadyHeld(JobRecord Job) : QueueVerbOutcome(Job);

public sealed record JobReleased(JobRecord Job) : QueueVerbOutcome(Job);

public sealed record JobNotHeld(JobRecord Job) : QueueVerbOutcome(Job);

/// <param name="WasHeld">The job was held, and moving it next released it.</param>
/// <param name="RunTakesIt">A run going now takes each next video from the queue as it is, so this one comes after the video converting now. A run from an older version keeps the videos it started with.</param>
public sealed record JobMovedNext(JobRecord Job, bool WasHeld, bool RunTakesIt) : QueueVerbOutcome(Job);

public enum VerbRefusal
{
    /// <summary>A run holds the run lock, so the job really is converting.</summary>
    ConvertingNow,
    /// <summary>A finalisation, stopped or left by a crash: finalize checks and resumes it. <c>reject</c> also says it of a ready Immich job whose finalisation stopped partway, which may have uploaded the AV1 already.</summary>
    StoppedWhileFinalising,
    /// <summary>A finalise is going on it now.</summary>
    BeingFinalised,
    /// <summary>Neither waiting nor stopped (ready, failed, dropped, ...): the job's own status says what it is.</summary>
    NotWaiting,
    /// <summary>Not ready to finalise (waiting, converting, stopped): the job's own status says what it is. <c>reject</c> judges a finished AV1, which only a ready job has.</summary>
    NotReady,
    /// <summary><c>dismiss</c> of a job that did not fail: the job's own status says what it is.</summary>
    NotFailed
}

/// <summary>The verb did not act. <c>hold</c>, <c>next</c>, <c>drop</c> and <c>reject</c> refuse a finalisation; <c>hold</c>, <c>next</c> and <c>drop</c> also a job converting now; <c>hold</c> and <c>next</c> one that is not waiting or stopped; <c>reject</c> one that is not ready.</summary>
public sealed record VerbRefused(JobRecord Job, VerbRefusal Reason) : QueueVerbOutcome(Job);

/// <summary><c>queue drop</c> or <c>reject</c> of a job that had ended already. Whether cleanup would delete some of its files is asked after, by <see cref="QueueVerbs.CleanupDeletesFilesAsync"/>.</summary>
public sealed record JobAlreadyEnded(JobRecord Job) : QueueVerbOutcome(Job);

/// <summary>A finalise holds the finalise lock, and a finished AV1 is not dropped or rejected under it.</summary>
public sealed record FinaliseRunning(JobRecord Job) : QueueVerbOutcome(Job);

/// <summary>Cleanup deletes a dropped job's AV1 and downloaded copy, and a reject deletes them itself; they may be the only copies, so the drop or reject is refused.</summary>
public sealed record OnlyCopyAtRisk(JobRecord Job, OnlyCopyReason Reason, string? Detail = null) : QueueVerbOutcome(Job);

public enum RemoteStage
{
    CrfSearch,
    Encode
}

/// <summary>A remote CRF search or encode runs detached, so a stop leaves it going and dropping the job does not stop it; the next run on the worker does.</summary>
public sealed record RemoteStageNote(RemoteStage Stage);

public enum LeftAv1
{
    None,
    Finished,
    Unfinished
}

/// <summary>What of a dropped job cleanup will delete, read from cleanup's own plan so it cannot promise more.</summary>
public sealed record LeftFiles(LeftAv1 Av1, bool DownloadedCopy);

/// <summary>
/// The drop is written. What cleanup will leave is read after, by <see cref="QueueVerbs.LeftFilesAsync"/>, so the CLI can
/// report the drop before a read that could throw.
/// </summary>
/// <param name="RemoteStage">Set when the job's remote stage may still be running.</param>
/// <param name="HadFinishedAv1">Whether the job had a finished AV1 before the drop; any other AV1 file of it is unfinished.</param>
/// <param name="UploadedAssetId">The AV1 already uploaded to Immich, which a drop leaves there.</param>
public sealed record JobDropped(JobRecord Job, RemoteStageNote? RemoteStage, bool HadFinishedAv1, string? UploadedAssetId) : QueueVerbOutcome(Job);

/// <summary>What of a rejected job's files are meant: its AV1 (with its manifest and any earlier attempt's partial file) and, for an Immich video, the downloaded copy.</summary>
public sealed record RejectedFiles(bool Av1, bool DownloadedCopy)
{
    public bool Any => Av1 || DownloadedCopy;
}

/// <summary>
/// The reject is written and its files were deleted, except any that could not be (a player has the AV1 open): those are
/// <paramref name="LeftForCleanup"/>, which <c>cleanup</c> deletes later. Null when nothing was left. A file that was not
/// there to begin with, because the user moved it away, is in neither.
/// </summary>
/// <param name="Deleted">What the reject deleted.</param>
public sealed record JobRejected(JobRecord Job, RejectedFiles Deleted, RejectedFiles? LeftForCleanup) : QueueVerbOutcome(Job);

/// <summary><c>queue dismiss</c> hid the failure from the TUI's Failed group; the job is still failed.</summary>
public sealed record JobDismissed(JobRecord Job) : QueueVerbOutcome(Job);

public sealed record JobAlreadyDismissed(JobRecord Job) : QueueVerbOutcome(Job);

/// <summary>One step of <c>queue drop --all</c>, in the order it happened, so the CLI prints each as it goes.</summary>
public abstract record DropAllStep;

public sealed record NothingToDrop : DropAllStep;

public sealed record DroppedInBulk(JobRecord Job, RemoteStageNote? RemoteStage) : DropAllStep;

/// <summary>A run started the job, or a verb from another terminal changed it, since the list was read. The rest still go.</summary>
public sealed record ChangedUnderDropAll(Guid JobId) : DropAllStep;

/// <summary>A finalisation, stopped, is left to finalize.</summary>
public sealed record LeftStoppedFinalising(JobRecord Job) : DropAllStep;

/// <summary>A job stopped while its AV1 was checked is left, since a finished AV1 is dropped by ID only.</summary>
public sealed record LeftStoppedChecking(JobRecord Job) : DropAllStep;
