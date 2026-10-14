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

    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped;
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
    Task<JobRecord> CreateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
    Task UpdateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
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
    Task<JobProcessingResult> ProcessAsync(
        string databasePath,
        string sourcePath,
        AppSettings settings,
        bool force,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Whether <see cref="ProcessAsync"/> would reach a remote stage for this original. Read-only: a run asks before creating a server.</summary>
    Task<bool> NeedsWorkerAsync(string databasePath, string sourcePath, AppSettings settings, bool force, CancellationToken cancellationToken = default);
}

public sealed record QueueDiscoveryResult(IReadOnlyList<string> QueuedPaths, int AlreadyQueued, int Issues, int CacheHits = 0, int RealProbes = 0, int AlreadyConverted = 0, int PreviouslyFailed = 0, int PreviouslySkipped = 0);
public sealed record FailedQueueJob(Guid Id, string SourcePath, string FailureCategory, string FailureMessage);

/// <summary>A job that ended <see cref="JobStatus.Skipped"/>. Not a failure: it changes neither the exit code nor the managed worker's deletion.</summary>
public sealed record SkippedQueueJob(Guid Id, string SourcePath, string Message);

/// <param name="SkippedJobs">Started jobs left untouched because <c>--local</c> or <c>--remote</c> chose the other mode; see
/// <see cref="JobCompatibility.IsBoundToOtherMode"/>. They are neither runnable nor failed.</param>
/// <param name="InterruptedFinalizations">File jobs whose finalisation Ctrl+C or a crash interrupted. Left as they are: only
/// <c>finalize</c> can tell whether anything moved (VOPT-70).</param>
public sealed record QueuePreparationResult(IReadOnlyList<JobRecord> RunnableJobs, IReadOnlyList<FailedQueueJob> FailedJobs, IReadOnlyList<JobRecord> SkippedJobs, IReadOnlyList<JobRecord> InterruptedFinalizations);

/// <param name="UnreachableWorkerJob">The job the run stopped at because the worker could not be reached, or null. Every later
/// job would fail the same way, so the run ends there (VOPT-69). The job is <see cref="JobStatus.Interrupted"/> and resumes from
/// its stage, so it is not in <paramref name="FailedJobs"/>.</param>
/// <param name="NotStarted">The jobs after <paramref name="UnreachableWorkerJob"/> that the run never started; they stay queued.</param>
public sealed record QueueRunResult(int ReadyToFinalize, IReadOnlyList<FailedQueueJob> FailedJobs, IReadOnlyList<SkippedQueueJob> SkippedJobs, JobRecord? UnreachableWorkerJob, int NotStarted, ExitCode ExitCode)
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
    /// Readies the open jobs of one source for processing: <c>queue run</c> takes file jobs, <c>immich run</c> Immich jobs.
    /// Every source's Immich finalisation that a crash interrupted goes back to ready either way.
    /// </summary>
    /// <param name="modeOverridden">Whether <c>--local</c> or <c>--remote</c> set the mode in <paramref name="settings"/>. The settings
    /// alone cannot say so, and only then is a started job from the other mode skipped instead of failed.</param>
    Task<QueuePreparationResult> PrepareAsync(string databasePath, AppSettings settings, JobSourceKind sourceKind, bool modeOverridden = false, CancellationToken cancellationToken = default);
    Task<QueueRunResult> RunAsync(string databasePath, AppSettings settings, IReadOnlyList<JobRecord> jobs, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
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
