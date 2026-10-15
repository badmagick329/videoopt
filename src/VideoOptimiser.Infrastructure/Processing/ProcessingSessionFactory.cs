using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Tools;

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
            return new LocalProcessingSession(job, crfSearchFactory(settings.Tools.AbAv1Path), encoderFactory(settings.Tools.AbAv1Path));
        }

        return new RemoteSshProcessingSession(job, settings, processes);
    }
}

/// <summary>
/// Runs ab-av1 in this process. It keeps every line ab-av1 printed, because ab-av1 exits with a one-line error and the console
/// shows only translated progress: when a stage fails, the full log is written beside the original (the place the remote
/// session already retains its logs) and named in the exception, so the failure points at the evidence.
/// </summary>
internal sealed class LocalProcessingSession(JobRecord job, ICrfSearchClient crfSearch, IVideoEncoder encoder) : IProcessingSession
{
    public bool IsRemote => false;
    public string ExecutionMode => "local";
    public string? RemoteHost => null;
    public string? RemoteWorkspace => null;

    public Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunLoggedAsync(sourcePath, "crf", progress, recorder => crfSearch.SearchAsync(sourcePath, settings, recorder, cancellationToken));

    public async Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = await RunLoggedAsync(sourcePath, $"encode-{attempt.ToString(CultureInfo.InvariantCulture)}", progress, recorder => encoder.EncodeAsync(sourcePath, outputPath, crf, settings, flacAudioStreams, recorder, cancellationToken));
        return new ProcessingStageResult(result.OutputPath, result.Duration);
    }

    public Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<T> RunLoggedAsync<T>(string sourcePath, string stageName, IProgress<JobProgress>? progress, Func<IProgress<JobProgress>, Task<T>> run)
    {
        var recorder = new LogRecorder(progress);
        try
        {
            return await run(recorder);
        }
        // A skip is an expected outcome and cancellation is the user's choice; neither needs a log.
        catch (Exception exception) when (exception is not (OperationCanceledException or NoWorthwhileCrfException or AbAv1StageException) && recorder.HasLines)
        {
            throw new AbAv1StageException(exception.Message, recorder.TryWrite(Path.Combine(Path.GetDirectoryName(sourcePath)!, TemporaryOutputClassifier.FolderName), $"{job.Id:N}.{stageName}.log"), exception);
        }
    }

    /// <summary>Forwards each line and keeps the raw ones: ab-av1's stdout and stderr arrive on separate tasks.</summary>
    private sealed class LogRecorder(IProgress<JobProgress>? inner) : IProgress<JobProgress>
    {
        private readonly List<string> _lines = [];

        public bool HasLines { get { lock (_lines) return _lines.Count > 0; } }

        public void Report(JobProgress value)
        {
            if (value.Kind == JobProgressKind.ToolOutput) lock (_lines) _lines.Add(value.Text);
            inner?.Report(value);
        }

        /// <summary>The written file's path, or null when it could not be written: the failure message matters more than its log.</summary>
        public string? TryWrite(string directory, string fileName)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, fileName);
                string[] lines;
                lock (_lines) lines = [.. _lines];
                File.WriteAllLines(path, lines);
                return path;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
