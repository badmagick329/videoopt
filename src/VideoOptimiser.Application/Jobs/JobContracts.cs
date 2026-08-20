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
    Downloading = 11
}

public sealed class JobRecord
{
    public Guid Id { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
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
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string ExecutionMode { get; set; } = "local";
    public string? RemoteHost { get; set; }
    public string? RemoteWorkspace { get; set; }

    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;
}

public sealed record JobProcessingResult(JobRecord Job, ExitCode ExitCode, string Message);

public static class JobStateTransitions
{
    public static bool IsAllowed(JobStatus current, JobStatus next) => current == next || (current, next) switch
    {
        (JobStatus.Queued, JobStatus.Staging or JobStatus.CrfSearching or JobStatus.Failed or JobStatus.Interrupted or JobStatus.Cancelled) => true,
        (JobStatus.Staging, JobStatus.CrfSearching or JobStatus.Encoding or JobStatus.Validating or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.CrfSearching, JobStatus.Encoding or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.Encoding, JobStatus.Downloading or JobStatus.Validating or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.Downloading, JobStatus.Validating or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.Validating, JobStatus.ReadyToFinalize or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.ReadyToFinalize, JobStatus.Validating or JobStatus.Finalizing or JobStatus.Failed) => true,
        (JobStatus.Finalizing, JobStatus.Completed or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.Interrupted, JobStatus.Queued or JobStatus.Staging or JobStatus.CrfSearching or JobStatus.Encoding or JobStatus.Downloading or JobStatus.Validating or JobStatus.Failed or JobStatus.Cancelled) => true,
        _ => false
    };
}

public interface IJobRepository
{
    Task<JobRecord?> FindActiveAsync(string databasePath, string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default);
    Task<JobRecord?> FindOpenBySourceAsync(string databasePath, string sourcePath, CancellationToken cancellationToken = default);
    Task<JobRecord?> GetAsync(string databasePath, Guid id, CancellationToken cancellationToken = default);
    Task<JobRecord> CreateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
    Task UpdateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRecord>> ListAsync(string databasePath, bool terminal, CancellationToken cancellationToken = default);
    Task MarkActiveJobsInterruptedAsync(string databasePath, CancellationToken cancellationToken = default);
}

public interface IOutputValidationService
{
    Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IJobProcessor
{
    Task<JobProcessingResult> ProcessAsync(
        string databasePath,
        string sourcePath,
        AppSettings settings,
        bool force,
        IProgress<CrfSearchOutput>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record QueueDiscoveryResult(IReadOnlyList<string> QueuedPaths, int AlreadyQueued, int Issues, int CacheHits = 0, int RealProbes = 0);
public sealed record QueueRunResult(int ReadyToFinalize, int Failed, ExitCode ExitCode);

public interface IQueueService
{
    Task<QueueDiscoveryResult> DiscoverAsync(string databasePath, AppSettings settings, bool first, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<QueueRunResult> RunAsync(string databasePath, AppSettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record FinalizationResult(JobRecord? Job, ExitCode ExitCode, string Message);

public interface IFinalizationService
{
    Task<FinalizationResult> FinalizeAsync(string databasePath, Guid jobId, AppSettings settings, CancellationToken cancellationToken = default);
}
