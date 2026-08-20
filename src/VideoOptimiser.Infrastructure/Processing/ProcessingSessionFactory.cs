using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.Infrastructure.Processing;

public sealed class ProcessingSessionFactory(
    Func<string, ICrfSearchClient> crfSearchFactory,
    Func<string, IVideoEncoder> encoderFactory,
    IExternalProcessRunner processes) : IProcessingSessionFactory
{
    public IProcessingSession Create(JobRecord job, AppSettings settings)
    {
        if (!string.Equals(settings.Processing.Mode, "remoteSsh", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalProcessingSession(crfSearchFactory(settings.Tools.AbAv1Path), encoderFactory(settings.Tools.AbAv1Path));
        }

        return new RemoteSshProcessingSession(job, settings, processes);
    }
}

internal sealed class LocalProcessingSession(ICrfSearchClient crfSearch, IVideoEncoder encoder) : IProcessingSession
{
    public bool IsRemote => false;
    public string ExecutionMode => "local";
    public string? RemoteHost => null;
    public string? RemoteWorkspace => null;

    public Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default) => crfSearch.SearchAsync(sourcePath, settings, progress, cancellationToken);

    public async Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = await encoder.EncodeAsync(sourcePath, outputPath, crf, settings, progress, cancellationToken);
        return new ProcessingStageResult(result.OutputPath, result.Duration);
    }

    public Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
