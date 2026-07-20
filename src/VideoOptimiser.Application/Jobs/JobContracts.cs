using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Jobs;

public enum JobStatus
{
    Queued,
    CrfSearching,
    Encoding,
    Validating,
    ReadyToFinalize,
    Finalizing,
    Completed,
    Failed,
    Interrupted
}

public sealed class JobRecord
{
    public Guid Id { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
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

    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Interrupted;
}

public sealed record JobProcessingResult(JobRecord Job, ExitCode ExitCode, string Message);

public static class JobStateTransitions
{
    public static bool IsAllowed(JobStatus current, JobStatus next) => current == next || (current, next) switch
    {
        (JobStatus.Queued, JobStatus.CrfSearching or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.CrfSearching, JobStatus.Encoding or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.Encoding, JobStatus.Validating or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.Validating, JobStatus.ReadyToFinalize or JobStatus.Failed or JobStatus.Interrupted) => true,
        (JobStatus.ReadyToFinalize, JobStatus.Validating or JobStatus.Finalizing or JobStatus.Failed) => true,
        (JobStatus.Finalizing, JobStatus.Completed or JobStatus.Failed or JobStatus.Interrupted) => true,
        _ => false
    };
}

public interface IJobRepository
{
    Task<JobRecord?> FindActiveAsync(string databasePath, string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default);
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
