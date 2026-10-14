using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Jobs;

public enum JobStatus
{
    Queued = 0,
    CrfSearching = 1,
    Encoding = 2,
    Validating = 3,
    ReadyToFinalize = 4,
    Finalizing = 5,
    Completed = 6,
    Failed = 7,
    Interrupted = 8,
    Cancelled = 9,
    Staging = 10,
    Downloading = 11,
    /// <summary>Ended without converting because AV1 would not save enough. Terminal, and not a failure.</summary>
    Skipped = 12
}

/// <summary>Where a job's original came from. Stored as <c>file</c> or <c>immich</c>.</summary>
public enum JobSourceKind
{
    File = 0,
    Immich = 1
}

public sealed class JobRecord
{
    public Guid Id { get; set; }
    /// <summary>The file the pipeline reads. For an Immich job this is the staged original, a disposable local copy.</summary>
    public string SourcePath { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
    public JobSourceKind SourceKind { get; set; } = JobSourceKind.File;
    /// <summary>An Immich job's identity: the original asset, which outlives the staged copy at <see cref="SourcePath"/>.</summary>
    public string? ImmichAssetId { get; set; }
    /// <summary>The original's base64 SHA-1 exactly as Immich returned it; the re-upload guard matches on it.</summary>
    public string? ImmichChecksum { get; set; }
    /// <summary>Set as soon as finalisation's upload succeeds, so a resumed finalisation continues with that asset.</summary>
    public string? ImmichNewAssetId { get; set; }
    public JobStatus Status { get; set; }
    public JobStatus? ResumeStatus { get; set; }
    public int Attempt { get; set; }
    public int? Crf { get; set; }
    public string? OutputPath { get; set; }
    public string? ManifestPath { get; set; }
    public bool? ValidationPassed { get; set; }
    public long? SourceSizeBytes { get; set; }
    public long? OutputSizeBytes { get; set; }
    public decimal? PercentageSaved { get; set; }
    public string? FailureCategory { get; set; }
    public string? FailureMessage { get; set; }
    /// <summary>
    /// The <see cref="Processing.QualityFingerprint"/> of the settings that judged the video: set when the CRF search chooses
    /// a CRF, or when the job ends <see cref="JobStatus.Skipped"/>. Null on jobs converted before calibrate, which therefore
    /// leaves them out of its history.
    /// </summary>
    public string? QualityFingerprint { get; set; }
    /// <summary>Set from the probe that queued the job, and again from the one each run makes. Null on jobs from before migration 8 that no probe cache entry matched.</summary>
    public VideoFacts? VideoFacts { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string ExecutionMode { get; set; } = "local";
    public string? RemoteHost { get; set; }
    public string? RemoteWorkspace { get; set; }
    /// <summary><c>queue hold</c>: runs pass over the job, and nothing but the queue verbs writes it until it is released.</summary>
    public bool Held { get; set; }
    /// <summary><c>queue next</c>: the run order sorts on it, highest first, before <see cref="CreatedUtc"/>.</summary>
    public int Priority { get; set; }
    /// <summary><c>queue add &lt;file&gt; --force</c>: the run skips the watch-root and eligibility checks for it.</summary>
    public bool Forced { get; set; }
    /// <summary>
    /// The version of the row this copy was read at. Every write requires it unchanged and raises it, so a copy that another
    /// process changed since (a drop during a run, a hold from a second terminal) fails with <see cref="JobChangedException"/>.
    /// </summary>
    public long RowVersion { get; set; }

    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped;

    /// <summary>In the queue: a run may start it, now or once it is released. An interrupted finalisation belongs to finalize.</summary>
    public bool IsQueued => Status is JobStatus.Queued or JobStatus.Interrupted && ResumeStatus != JobStatus.Finalizing;
}

/// <summary>The order a run takes queued jobs in: <c>queue next</c> first, latest first, then oldest queued first.</summary>
public static class JobRunOrder
{
    public static IOrderedEnumerable<JobRecord> Sort(IEnumerable<JobRecord> jobs) =>
        jobs.OrderByDescending(job => job.Priority).ThenBy(job => job.CreatedUtc);
}

/// <summary>
/// A write lost to a change made since the job was read: another process wrote it (a queue verb, a run), or it is held. The
/// writer passes over the job, or tells the user to try again; it never overwrites the change.
/// </summary>
public sealed class JobChangedException(Guid jobId) : InvalidOperationException($"Job {jobId:N} changed while this command was using it. Run the command again.")
{
    public Guid JobId { get; } = jobId;
}

/// <summary>A new job for an original that already has an open one. Callers check first; this catches the race between two commands.</summary>
public sealed class OpenJobExistsException(string sourcePath) : InvalidOperationException($"An open job already exists for {sourcePath}.")
{
    public string SourcePath { get; } = sourcePath;
}

public sealed record JobProcessingResult(JobRecord Job, ExitCode ExitCode, string Message);

/// <summary>
/// Ctrl+C stopped a job. It carries the job and the stage it stopped in, so the CLI can say what is still running on the
/// worker: a run of several jobs cannot tell which <see cref="JobStatus.Interrupted"/> job was the one in progress.
/// </summary>
public sealed class JobInterruptedException(JobRecord job, JobStatus stage, bool remote, CancellationToken cancellationToken)
    : OperationCanceledException("Processing was cancelled.", cancellationToken)
{
    public JobRecord Job { get; } = job;
    public JobStatus Stage { get; } = stage;
    public bool Remote { get; } = remote;
}

public static class JobStateTransitions
{
    public static bool IsAllowed(JobStatus current, JobStatus next) => current == next || (current, next) switch
    {
        // A resumed local job skips the stages it already finished: encoding when its CRF is known, validation when its output is.
        // Skipped from Queued: the video's size cap rules out every CRF before anything is staged or searched.
        (JobStatus.Queued, JobStatus.Staging or JobStatus.CrfSearching or JobStatus.Encoding or JobStatus.Validating or JobStatus.Skipped or JobStatus.Failed or JobStatus.Interrupted or JobStatus.Cancelled) => true,
        (JobStatus.Staging, JobStatus.CrfSearching or JobStatus.Encoding or JobStatus.Validating or JobStatus.Failed or JobStatus.Interrupted) => true,
        // Skipped from CrfSearching: no CRF both met the VMAF and stayed within the size cap.
        (JobStatus.CrfSearching, JobStatus.Encoding or JobStatus.Skipped or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.Encoding, JobStatus.Downloading or JobStatus.Validating or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.Downloading, JobStatus.Validating or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.Validating, JobStatus.ReadyToFinalize or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.ReadyToFinalize, JobStatus.Validating or JobStatus.Finalizing or JobStatus.Failed or JobStatus.Cancelled) => true,
        // A finalisation that stops before completing returns to ready, from Finalizing or after a crash from Interrupted:
        // each Immich step can run again, and a file job returns only when nothing moved (VOPT-70).
        (JobStatus.Finalizing, JobStatus.ReadyToFinalize or JobStatus.Completed or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.Interrupted, JobStatus.ReadyToFinalize or JobStatus.Queued or JobStatus.Staging or JobStatus.CrfSearching or JobStatus.Encoding or JobStatus.Downloading or JobStatus.Validating or JobStatus.Failed or JobStatus.Cancelled) => true,
        _ => false
    };
}

public interface IJobRepository
{
    Task<JobRecord?> FindOpenBySourceAsync(string databasePath, string sourcePath, CancellationToken cancellationToken = default);
    Task<JobRecord?> GetAsync(string databasePath, Guid id, CancellationToken cancellationToken = default);
    /// <summary>Inserts the job unless its original (path, or Immich asset) already has an open one: then <see cref="OpenJobExistsException"/>.</summary>
    Task<JobRecord> CreateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
    /// <summary>
    /// Writes every column but the queue verbs' (<see cref="JobRecord.Held"/>, <see cref="JobRecord.Priority"/>), if the row
    /// is still at <see cref="JobRecord.RowVersion"/> and not held; otherwise <see cref="JobChangedException"/>.
    /// </summary>
    Task UpdateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
    /// <summary><c>queue hold</c> and <c>release</c>. False when the row changed since <paramref name="job"/> was read.</summary>
    Task<bool> SetHeldAsync(string databasePath, JobRecord job, bool held, CancellationToken cancellationToken = default);
    /// <summary><c>queue next</c>: above every open job's priority, and released. False when the row changed since it was read.</summary>
    Task<bool> RunNextAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
    /// <summary>
    /// <c>queue drop</c>: <see cref="JobStatus.Cancelled"/> and released, held or not, keeping <see cref="JobRecord.ResumeStatus"/>
    /// so cleanup still recognises an interrupted finalisation. False when the row changed since it was read.
    /// </summary>
    Task<bool> DropAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRecord>> ListAsync(string databasePath, bool terminal, CancellationToken cancellationToken = default);
    /// <summary>
    /// Marks jobs a dead process left mid-stage <see cref="JobStatus.Interrupted"/>, so they resume from that stage. Only the
    /// holder of the run lock may call it. <paramref name="includeFinalizing"/> is false while a finalisation in another
    /// process holds its lock: that <see cref="JobStatus.Finalizing"/> job is live, not left behind.
    /// </summary>
    Task MarkActiveJobsInterruptedAsync(string databasePath, bool includeFinalizing, CancellationToken cancellationToken = default);

    /// <summary>The newest job for the asset that is not completed, failed, cancelled or skipped.</summary>
    Task<JobRecord?> FindOpenByImmichAssetAsync(string databasePath, string assetId, CancellationToken cancellationToken = default);

    /// <summary>Completed Immich jobs, oldest first. The re-upload guard looks for their original checksums coming back.</summary>
    Task<IReadOnlyList<JobRecord>> ListCompletedImmichAsync(string databasePath, CancellationToken cancellationToken = default);

    /// <summary>Every recorded skip, keyed by asset ID. Discovery reads them once per run and checks each with <see cref="ImmichSkipRecord.IsCurrent"/>.</summary>
    Task<IReadOnlyDictionary<string, ImmichSkipRecord>> ListImmichSkipsAsync(string databasePath, CancellationToken cancellationToken = default);

    /// <summary>Records or replaces the skip for <see cref="ImmichSkipRecord.AssetId"/>.</summary>
    Task PutImmichSkipAsync(string databasePath, ImmichSkipRecord skip, CancellationToken cancellationToken = default);
}

/// <summary>
/// An Immich asset that discovery downloaded and found ineligible, kept so later runs skip it without downloading it again.
/// <see cref="RulesFingerprint"/> identifies the eligibility settings that rejected it, so changing the rules lets the
/// asset be checked again. <see cref="VideoFacts"/> is null when the asset was ruled out without a download, and on
/// skips recorded before migration 8.
/// </summary>
public sealed record ImmichSkipRecord(string AssetId, string Checksum, string RulesFingerprint, string Reason, DateTimeOffset RecordedUtc, VideoFacts? VideoFacts = null)
{
    /// <summary>False once the asset's file or the eligibility rules have changed since the skip was recorded.</summary>
    public bool IsCurrent(string checksum, string rulesFingerprint) =>
        string.Equals(Checksum, checksum, StringComparison.Ordinal) && string.Equals(RulesFingerprint, rulesFingerprint, StringComparison.Ordinal);
}

public interface IOutputValidationService
{
    Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>A span of a video to decode; null bounds decode from the start or to the end.</summary>
public sealed record DecodeWindow(double? StartSeconds, double? DurationSeconds)
{
    public static DecodeWindow Whole { get; } = new(null, null);
}

public sealed record DecodeTestResult(bool Passed, string? Error);

public interface IMediaDecodeTester
{
    Task<DecodeTestResult> DecodeAsync(string path, DecodeWindow window, CancellationToken cancellationToken = default);
}

public interface IJobProcessor
{
    /// <summary>
    /// Runs a queued job, read just before, through the stages it has left. <see cref="JobRecord.Forced"/> skips the
    /// watch-root and rules checks. A write that finds the job changed since it was read (held or dropped from another
    /// terminal) throws <see cref="JobChangedException"/>, which the run takes as "pass over it".
    /// </summary>
    Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Whether <see cref="ProcessAsync"/> would reach a remote stage for this job. Read-only: a run asks before creating a server.</summary>
    Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default);
}

/// <param name="AlreadyQueued">Eligible files whose open job is waiting or stopped, so a run takes it.</param>
/// <param name="Held">Eligible files whose open job is held.</param>
/// <param name="ReadyToFinalize">Eligible files whose open job is ready to finalise, being finalised or stopped while it was.</param>
public sealed record QueueDiscoveryResult(IReadOnlyList<string> QueuedPaths, int AlreadyQueued, int Issues, int CacheHits = 0, int RealProbes = 0, int AlreadyConverted = 0, int PreviouslyFailed = 0, int PreviouslySkipped = 0, int Held = 0, int ReadyToFinalize = 0);

/// <summary>What <c>queue add &lt;file&gt;</c> found. Each outcome but <see cref="Queued"/> leaves the queue as it was.</summary>
public enum QueueAddOutcome
{
    Queued,
    Missing,
    /// <summary>A temporary output or a staged Immich copy: queueing it would convert the tool's own file.</summary>
    MadeByTool,
    Archived,
    OpenJob,
    /// <summary>Its latest job settled it (completed, failed, dropped, not worth converting) and the file is unchanged.</summary>
    Settled,
    OutsideRoots,
    Ineligible,
    TooSmall,
    Unreadable,
    ProbeFailed
}

/// <param name="Job">The new job, the open job, or the job that settled the file.</param>
/// <param name="Reason">Why the file is ineligible, too small, unreadable or unprobeable.</param>
/// <param name="OutsideRules">Queued only because <c>--force</c> lifted the watch-root or rule checks.</param>
public sealed record QueueAddResult(QueueAddOutcome Outcome, string Path, JobRecord? Job = null, string? Reason = null, bool OutsideRules = false);
public sealed record FailedQueueJob(Guid Id, string SourcePath, string FailureCategory, string FailureMessage);

/// <summary>A job that ended <see cref="JobStatus.Skipped"/>. Not a failure: it changes neither the exit code nor the managed worker's deletion.</summary>
public sealed record SkippedQueueJob(Guid Id, string SourcePath, string Message);

/// <param name="SkippedJobs">Started jobs left untouched because <c>--local</c> or <c>--remote</c> chose the other mode; see
/// <see cref="JobCompatibility.IsBoundToOtherMode"/>. They are neither runnable nor failed.</param>
/// <param name="InterruptedFinalizations">File jobs whose finalisation Ctrl+C or a crash interrupted. Left as they are: only
/// <c>finalize</c> can tell whether anything moved (VOPT-70).</param>
/// <param name="RunnableJobs">In run order: the run's first view of the queue.</param>
/// <param name="FollowQueue">The run takes each next video from the queue as it is then, so a video added, released or moved
/// next during the run joins it. False keeps the run to <paramref name="RunnableJobs"/>: <c>immich run</c>'s set, fixed by its
/// <c>--limit</c> (until phase A step 7).</param>
/// <param name="SourceKind">The source the run converts; null for every source (<c>run</c>).</param>
/// <param name="Held">The held videos of that source that a run would otherwise take.</param>
public sealed record QueuePreparationResult(IReadOnlyList<JobRecord> RunnableJobs, IReadOnlyList<FailedQueueJob> FailedJobs, IReadOnlyList<JobRecord> SkippedJobs, IReadOnlyList<JobRecord> InterruptedFinalizations, JobSourceKind? SourceKind = null, bool FollowQueue = true, int Held = 0);

/// <param name="UnreachableWorkerJob">The job the run stopped at because the worker could not be reached, or null. Every later
/// job would fail the same way, so the run ends there (VOPT-69). The job is <see cref="JobStatus.Interrupted"/> and resumes from
/// its stage, so it is not in <paramref name="FailedJobs"/>.</param>
/// <param name="NotStarted">The jobs after <paramref name="UnreachableWorkerJob"/> or <paramref name="StoppedAfter"/> that the run never started; they stay queued.</param>
/// <param name="StoppedAfter">The last job of a run that Ctrl+C stopped after it, with jobs left to start, or null.</param>
public sealed record QueueRunResult(int ReadyToFinalize, IReadOnlyList<FailedQueueJob> FailedJobs, IReadOnlyList<SkippedQueueJob> SkippedJobs, JobRecord? UnreachableWorkerJob, int NotStarted, ExitCode ExitCode, JobRecord? StoppedAfter = null)
{
    public int Failed => FailedJobs.Count;
}

public static class QueueExitCode
{
    /// <param name="workerUnreachable">Counts as one failure, so a run the worker stopped exits as it did when that job was
    /// listed among the failures.</param>
    public static ExitCode Calculate(int readyToFinalize, int failures, bool workerUnreachable = false) => failures == 0 && !workerUnreachable
        ? ExitCode.Success
        : readyToFinalize > 0 ? ExitCode.PartialSuccess : ExitCode.ProcessingFailure;
}

public sealed record JobRetryResult(JobRecord? Replacement, string? Error)
{
    public bool Succeeded => Replacement is not null;
}

public interface IQueueService
{
    Task<QueueDiscoveryResult> DiscoverAsync(string databasePath, AppSettings settings, bool first, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues one file after the checks a run makes: inside a watch root, eligible, worth converting. <paramref name="force"/>
    /// skips the watch-root and rule checks and is stored on the job; nothing admits a file the tool made or archived.
    /// </summary>
    Task<QueueAddResult> AddFileAsync(string databasePath, AppSettings settings, string path, bool force, CancellationToken cancellationToken = default);

    /// <summary>
    /// Readies the open jobs of one source, or of every source, for processing: <c>run</c> takes every job, <c>immich run</c>
    /// Immich jobs. Every source's Immich finalisation that a crash interrupted goes back to ready either way.
    /// </summary>
    /// <param name="sourceKind">The source to ready; null for every source.</param>
    /// <param name="modeOverridden">Whether <c>--local</c> or <c>--remote</c> set the mode in <paramref name="settings"/>. The settings
    /// alone cannot say so, and only then is a started job from the other mode skipped instead of failed.</param>
    Task<QueuePreparationResult> PrepareAsync(string databasePath, AppSettings settings, JobSourceKind? sourceKind, bool modeOverridden = false, CancellationToken cancellationToken = default);
    /// <summary>
    /// Converts the queued videos of <paramref name="preparation"/>'s source (every source when it has none), one at a time, each tried at most once: the
    /// first queued, unheld one in <see cref="JobRunOrder"/> that this run has not tried, read again as it starts.
    /// </summary>
    /// <param name="passOver">Asked before each video starts; true leaves it queued for a later run, after the caller has said why.</param>
    /// <param name="stopAfterThisVideo">Checked before each job, never during one, so a stop after this video keeps the current job's work.</param>
    Task<QueueRunResult> RunAsync(string databasePath, AppSettings settings, QueuePreparationResult preparation, Func<JobRecord, CancellationToken, Task<bool>>? passOver = null, IProgress<JobProgress>? progress = null, CancellationToken stopAfterThisVideo = default, CancellationToken cancellationToken = default);
}

public interface IJobRetryService
{
    Task<JobRetryResult> RetryAsync(string databasePath, Guid jobId, AppSettings settings, CancellationToken cancellationToken = default);
}

public sealed record FinalizationResult(JobRecord? Job, ExitCode ExitCode, string Message);

public interface IFinalizationService
{
    Task<FinalizationResult> FinalizeAsync(string databasePath, Guid jobId, AppSettings settings, CancellationToken cancellationToken = default);
}
