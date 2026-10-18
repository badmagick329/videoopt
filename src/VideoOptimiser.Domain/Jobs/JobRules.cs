namespace VideoOptimiser.Domain.Jobs;

/// <summary>The order a run takes queued jobs in: <c>queue next</c> first, latest first, then oldest queued first.</summary>
public static class JobRunOrder
{
    public static IOrderedEnumerable<JobRecord> Sort(IEnumerable<JobRecord> jobs) =>
        jobs.OrderByDescending(job => job.Priority).ThenBy(job => job.CreatedUtc);
}

public static class JobStateTransitions
{
    public static bool IsAllowed(JobStatus current, JobStatus next) => current == next || (current, next) switch
    {
        // A resumed local job skips the stages it already finished: encoding when its CRF is known, validation when its output is.
        // Skipped from Queued: the video's size cap rules out every CRF before anything is staged or searched.
        // Downloading from Queued: a resumed remote job whose encode the worker already finished has nothing to stage or encode.
        (JobStatus.Queued, JobStatus.Staging or JobStatus.CrfSearching or JobStatus.Encoding or JobStatus.Downloading or JobStatus.Validating or JobStatus.Skipped or JobStatus.Failed or JobStatus.Interrupted or JobStatus.Cancelled) => true,
        (JobStatus.Staging, JobStatus.CrfSearching or JobStatus.Encoding or JobStatus.Validating or JobStatus.Failed or JobStatus.Interrupted) => true,
        // Skipped from CrfSearching: no CRF both met the VMAF and stayed within the size cap.
        (JobStatus.CrfSearching, JobStatus.Encoding or JobStatus.Skipped or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.Encoding, JobStatus.Downloading or JobStatus.Validating or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.Downloading, JobStatus.Validating or JobStatus.Failed or JobStatus.Interrupted) => true,
        // Skipped from Validating: the finished AV1 saves less than the savings settings ask (VOPT-117).
        (JobStatus.Validating, JobStatus.ReadyToFinalize or JobStatus.Skipped or JobStatus.Failed or JobStatus.Interrupted) => true,
        // Rejected only from ready: the user can judge an AV1 only once it has passed validation (VOPT-112).
        // Skipped from ready: validate checks the AV1 again under savings settings that now find it not worth keeping (VOPT-117).
        (JobStatus.ReadyToFinalize, JobStatus.Validating or JobStatus.Finalizing or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Rejected or JobStatus.Skipped) => true,
        // A finalisation that stops before completing returns to ready, from Finalizing or after a crash from Interrupted:
        // each Immich step can run again, and a file job returns only when nothing moved (VOPT-70).
        (JobStatus.Finalizing, JobStatus.ReadyToFinalize or JobStatus.Completed or JobStatus.Failed or JobStatus.Interrupted) => true,
        // Failed is final except for two gated paths. The table sees statuses only, so each gate lives in its own code and
        // nothing else may take a failed job back. VOPT-85: FinalizationService takes back a folder job that a Ctrl+C during
        // finalisation failed, only after it shows that the original and the AV1 are where they started. VOPT-119:
        // FailedValidationRecheck checks again a job that failed validation under older rules, only while its original is
        // unchanged since the encode; the new verdict makes it ready, or skipped when the AV1 is not worth keeping.
        (JobStatus.Failed, JobStatus.ReadyToFinalize or JobStatus.Skipped) => true,
        // Skipped from Interrupted: validate finishes a check that was stopped, and finds the AV1 not worth keeping (VOPT-117).
        (JobStatus.Interrupted, JobStatus.ReadyToFinalize or JobStatus.Queued or JobStatus.Staging or JobStatus.CrfSearching or JobStatus.Encoding or JobStatus.Downloading or JobStatus.Validating or JobStatus.Skipped or JobStatus.Failed or JobStatus.Cancelled) => true,
        _ => false
    };
}

/// <summary>
/// The <see cref="JobRecord.FailureCategory"/> values that more than one command reads. They are stored on the job, so
/// renaming one strands the jobs that already carry it.
/// </summary>
public static class FailureCategories
{
    /// <summary>An open job's original was gone when it was about to run. Its asset or file is queued again once it is back.</summary>
    public const string SourceMissing = "SourceMissing";

    public const string SourceNotReady = "SourceNotReady";

    /// <summary>An <see cref="JobStatus.Interrupted"/> job whose worker stopped answering; it resumes from its stage.</summary>
    public const string RemoteUnavailable = "RemoteUnavailable";

    /// <summary>
    /// A <see cref="JobStatus.Skipped"/> job: AV1 cannot meet the quality bar and the size cap together, or the finished AV1
    /// saves less than the savings settings ask.
    /// </summary>
    public const string NotWorthConverting = "NotWorthConverting";

    /// <summary>
    /// A <see cref="JobStatus.Skipped"/> job whose audio FFmpeg cannot copy (VOPT-125): <c>quality.unsupportedAudio</c> is
    /// <c>skip</c>, or no audio track would be left. Nothing was staged or encoded. Settled like any skip, by the quality
    /// fingerprint, which carries the setting while it is <c>skip</c>.
    /// </summary>
    public const string UnsupportedAudio = "UnsupportedAudio";

    /// <summary>
    /// A <see cref="JobStatus.Failed"/> job whose AV1 left nothing to review: missing, empty, unreadable or not AV1. Before
    /// VOPT-117 every doubtful check failed a job this way, so a run checks such jobs again while their AV1 is on disk.
    /// </summary>
    public const string ValidationFailed = "ValidationFailed";

    /// <summary>A <see cref="JobStatus.Rejected"/> job. The status says it all, so no list adds a phrase for it.</summary>
    public const string Rejected = "Rejected";

    /// <summary>A ready Immich job whose last finalisation stopped early carries this category until it completes.</summary>
    public const string FinalizationIncomplete = "FinalizationIncomplete";

    /// <summary>A file finalisation that was cut off after something moved; its kept resume status stops cleanup deleting the AV1.</summary>
    public const string ManualInterventionRequired = "ManualInterventionRequired";

    /// <summary>
    /// Failures that judged the file's state or the settings at run time rather than an attempt to convert it. Discovery has
    /// just found the file eligible inside a watch root, so the earlier verdict no longer holds and the file is queued again.
    /// </summary>
    public static readonly IReadOnlySet<string> Requeued = new HashSet<string>(StringComparer.Ordinal) { SourceNotReady, "Ineligible", "SourceOutsideWatchRoot", SourceMissing };
}

/// <summary>The rules for a finalisation that stopped before completing, which finalize, a run and the queue all apply.</summary>
public static class FinalizationRules
{
    /// <summary>What a ready job says about its stopped finalisation. The caller words the command, because only the CLI knows a typed <c>--config</c>.</summary>
    public static string InterruptedMessage(string resumeCommand) => $"Finalisation was interrupted. Run '{resumeCommand}' again to resume.";
    public const string ManualReviewMessage = "Finalisation was interrupted and requires manual review.";

    /// <summary>
    /// An Immich job that a crash left mid-finalisation: still <see cref="JobStatus.Finalizing"/>, or marked interrupted on
    /// its way out of it. Unlike a local file, nothing is half-moved, so it is ready again rather than needing review.
    /// </summary>
    public static bool IsInterruptedImmich(JobRecord job) => job.SourceKind == JobSourceKind.Immich && IsInterrupted(job);

    /// <summary>
    /// A file job that Ctrl+C or a crash left mid-finalisation. Finalize resumes it only after checking that nothing moved;
    /// otherwise it needs review (VOPT-70).
    /// </summary>
    public static bool IsInterruptedFile(JobRecord job) => job.SourceKind == JobSourceKind.File && IsInterrupted(job);

    /// <summary>
    /// A file job that failed for review after its finalisation was cut off: an older version failed it even when nothing had
    /// moved, and the review path still does when something did. Only <c>finalize &lt;id&gt;</c> re-checks it, since a
    /// job in this state may have nothing left to finalise; <c>finalize --ready</c> and runs leave it alone (VOPT-85).
    /// </summary>
    public static bool IsFailedFileReview(JobRecord job) =>
        job is { SourceKind: JobSourceKind.File, Status: JobStatus.Failed, ResumeStatus: JobStatus.Finalizing, FailureCategory: FailureCategories.ManualInterventionRequired };

    /// <summary>What finalising one video accepts: a ready video, or one whose finalisation was interrupted.</summary>
    public static bool IsFinalisable(JobRecord job) => job.Status == JobStatus.ReadyToFinalize || IsInterruptedImmich(job) || IsInterruptedFile(job);

    /// <summary>
    /// A ready video whose validation left warnings. Bulk finalise leaves it out, so each one is finalised on purpose with
    /// <c>finalize &lt;id&gt;</c> or the TUI's <c>f</c> after the user has watched it (VOPT-116). A finalisation that was
    /// already under way stays in: its user already chose to finalise it.
    /// </summary>
    public static bool IsLeftForReview(JobRecord job) =>
        job is { Status: JobStatus.ReadyToFinalize, ValidationWarnings.Count: > 0 } && job.FailureCategory != FailureCategories.FinalizationIncomplete;

    /// <summary>What <c>finalize --ready</c>, <c>immich finalize</c> and the TUI's <c>F</c> take.</summary>
    public static bool IsBulkFinalisable(JobRecord job) => IsFinalisable(job) && !IsLeftForReview(job);

    private static bool IsInterrupted(JobRecord job) =>
        job.Status == JobStatus.Finalizing || job.Status == JobStatus.Interrupted && job.ResumeStatus == JobStatus.Finalizing;

    public static void ReturnToReady(JobRecord job, string message)
    {
        job.Status = JobStatus.ReadyToFinalize;
        job.ResumeStatus = null;
        job.FailureCategory = FailureCategories.FinalizationIncomplete;
        job.FailureMessage = message;
    }

    /// <summary>The first sentence of F4; <c>validate</c> prints it alone.</summary>
    public static string OriginalMissing(JobRecord job) => $"Original does not exist: {job.SourcePath}.";

    /// <summary>The first sentence of F1; <c>validate</c> prints it alone.</summary>
    public static string Av1Missing(JobRecord job) => $"The AV1 is missing: {job.OutputPath}.";
}
