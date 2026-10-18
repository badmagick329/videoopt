namespace VideoOptimiser.Domain.Jobs;

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
    /// <summary>
    /// What the last validation asked the user to check before finalising (VOPT-116), one line each; empty when it found
    /// nothing or never ran. Bulk finalise leaves a job with any out.
    /// </summary>
    public IReadOnlyList<string> ValidationWarnings { get; set; } = [];
    /// <summary>
    /// What the last attempt left out or changed that the user should know (VOPT-125), one line each; empty when nothing.
    /// Not a warning: it never holds a video back from bulk finalise and counts toward nothing. A new attempt replaces it.
    /// </summary>
    public IReadOnlyList<string> Notes { get; set; } = [];
    public long? SourceSizeBytes { get; set; }
    public long? OutputSizeBytes { get; set; }
    public decimal? PercentageSaved { get; set; }
    public string? FailureCategory { get; set; }
    public string? FailureMessage { get; set; }
    /// <summary>
    /// The <c>QualityFingerprint</c> of the settings that judged the video: set when the CRF search chooses a CRF, or when the
    /// job ends <see cref="JobStatus.Skipped"/>. Null on jobs converted before calibrate, which therefore leaves them out of
    /// its history.
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
    /// <c>queue dismiss</c> (VOPT-115): the user saw this failure and wants it out of the TUI's Failed group. The job stays
    /// <see cref="JobStatus.Failed"/> so its original stays settled; null on any other status.
    /// </summary>
    public DateTimeOffset? DismissedUtc { get; set; }
    /// <summary>
    /// The version of the row this copy was read at. Every write requires it unchanged and raises it, so a copy that another
    /// process changed since (a drop during a run, a hold from a second terminal) fails with <see cref="JobChangedException"/>.
    /// </summary>
    public long RowVersion { get; set; }

    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped or JobStatus.Rejected;

    /// <summary>In the queue: a run may start it, now or once it is released. An interrupted finalisation belongs to finalize.</summary>
    public bool IsQueued => Status is JobStatus.Queued or JobStatus.Interrupted && ResumeStatus != JobStatus.Finalizing;

    /// <summary>
    /// In a status a run sets while it works on the job. It is converting only while a run holds the run lock; otherwise a
    /// crash or a closed window left it so.
    /// </summary>
    public bool IsConverting => Status is JobStatus.Staging or JobStatus.CrfSearching or JobStatus.Encoding or JobStatus.Downloading or JobStatus.Validating;

    /// <summary>Ready, or stopped while its AV1 was checked: the AV1 is whole, and cleanup would delete it once the job ends.</summary>
    public bool HasFinishedAv1 => Status == JobStatus.ReadyToFinalize || this is { Status: JobStatus.Interrupted, ResumeStatus: JobStatus.Validating };
}

/// <summary>
/// What a probe said about a source when its job was created or its Immich asset skipped: the primary video stream's
/// codec, size and bitrate, and the source's duration and size. Kept on the row because the probe cache is keyed by path,
/// and an Immich job's path is a staged copy that cleanup deletes; <c>calibrate</c> judges the eligibility rules by them.
/// </summary>
public sealed record VideoFacts(string Codec, int? Width, int? Height, long? Bitrate, double? DurationSeconds, long SizeBytes);
