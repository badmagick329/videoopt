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
    Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
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
}

public interface IProcessingSessionFactory
{
    IProcessingSession Create(JobRecord job, AppSettings settings);
}

public sealed class RemoteConnectionException(string message, Exception? innerException = null) : IOException(message, innerException);
