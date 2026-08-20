using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Application.Processing;

public sealed record ProcessingStageResult(string OutputPath, TimeSpan Duration);

public interface IProcessingSession : IAsyncDisposable
{
    bool IsRemote { get; }
    string ExecutionMode { get; }
    string? RemoteHost { get; }
    string? RemoteWorkspace { get; }

    Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default);
    Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default);
    Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default);
    Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
    Task CleanupAsync(CancellationToken cancellationToken = default);
}

public interface IProcessingSessionFactory
{
    IProcessingSession Create(JobRecord job, AppSettings settings);
}

public sealed class RemoteConnectionException(string message, Exception? innerException = null) : IOException(message, innerException);
