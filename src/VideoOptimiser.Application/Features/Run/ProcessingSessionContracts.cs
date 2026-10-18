using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Run;

public sealed record ProcessingStageResult(string OutputPath, TimeSpan Duration);

public interface IProcessingSession : IAsyncDisposable
{
    bool IsRemote { get; }
    string ExecutionMode { get; }
    string? RemoteHost { get; }
    string? RemoteWorkspace { get; }

    Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default);
    Task StageAsync(string sourcePath, string sourceFingerprint, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default)
        => StageAsync(sourcePath, sourceFingerprint, cancellationToken);
    Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, AudioPlan audio, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>
    /// Whether the worker holds the finished encode of <paramref name="attempt"/>: the stage ended with exit code 0 and its output
    /// is there. A run that finds one goes straight to the download, without staging the original again. Always false on this PC
    /// and on a worker that lost the workspace, such as a deleted managed server.
    /// </summary>
    Task<bool> HasFinishedEncodeAsync(string outputPath, int attempt, CancellationToken cancellationToken = default);
    Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default);
    Task RetrieveOutputAsync(string outputPath, int attempt, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default)
        => RetrieveOutputAsync(outputPath, attempt, cancellationToken);
    Task CleanupAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the stage processes and removes the workspaces on the worker of the jobs in <paramref name="jobIds"/>; any other
    /// workspace is left alone. A stage runs detached, so a removed workspace alone would not end its CRF search or encode.
    /// Callers pass the jobs whose work there is no longer wanted: those this database knows ended, whose search or encode
    /// nothing else would ever end or free the disk of (VOPT-90), and a job whose original changed since its work there
    /// started (VOPT-138). Never an open job's workspace that a later run picks up, nor one of a job this database does not
    /// know, because a manual worker can be shared by two databases. Nothing on this PC.
    /// </summary>
    Task StopAndRemoveWorkspacesAsync(IReadOnlySet<Guid> jobIds, CancellationToken cancellationToken = default);
}

public interface IProcessingSessionFactory
{
    IProcessingSession Create(JobRecord job, AppSettings settings);
}

public sealed class RemoteConnectionException(string message, Exception? innerException = null) : IOException(message, innerException);
