using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Validate;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests;

// Stand-ins for the files, media tools and worker that JobProcessor drives, so a test runs the real processor without them.

/// <summary>Reports every file ready, or never ready, and counts the checks.</summary>
internal sealed class ReadableFiles(bool ready = true) : IFileReadinessService
{
    public int Calls { get; private set; }

    public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new FileReadinessResult(ready, ready ? "Readable." : "Not ready."));
    }
}

/// <summary>Probes by calling <paramref name="media"/>, which may throw, and counts the probes.</summary>
internal sealed class FuncProbe(Func<string, MediaInfo> media) : IMediaProbe
{
    public int Calls { get; private set; }

    public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(media(path));
    }
}

/// <summary>A 1080p h264 original of 120 s at the given bitrate; an encoded output (<c>.encoding.mp4</c>) probes as av1.</summary>
internal sealed class H264Probe(IReadOnlyList<string>? audioCodecs = null, long? bitrate = 10_000_000) : IMediaProbe
{
    public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MediaInfo(path.EndsWith(".encoding.mp4", StringComparison.OrdinalIgnoreCase) ? "av1" : "h264", 1, 1, 0, 0, 120, new FileInfo(path).Length, 1920, 1080, bitrate, audioCodecs));
}

/// <summary>Chooses CRF 42 at once, with no estimate.</summary>
internal sealed class FixedCrfSearch : ICrfSearchClient
{
    public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) => [];
    public Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CrfSearchResult(42, string.Empty, string.Empty, TimeSpan.Zero));
}

/// <summary>Writes a small output and keeps what it was asked for.</summary>
internal sealed class FakeEncoder : IVideoEncoder
{
    public int CallCount { get; private set; }
    public string? OutputPath { get; private set; }
    public IReadOnlyList<int>? FlacAudioStreams { get; private set; }

    public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams) => [];

    public async Task<EncodeResult> EncodeAsync(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        OutputPath = outputPath;
        FlacAudioStreams = flacAudioStreams;
        await File.WriteAllTextAsync(outputPath, "av1", cancellationToken);
        return new EncodeResult(outputPath, TimeSpan.Zero);
    }
}

/// <summary>Passes every output at 100 B -> 50 B.</summary>
internal sealed class PassingValidator : IOutputValidationService
{
    public Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ValidationReport { Passed = true, SourceSizeBytes = 100, OutputSizeBytes = 50, PercentageSaved = 50, ValidatedUtc = DateTimeOffset.UtcNow });
}

internal sealed class UnexpectedExternalProcessRunner : IExternalProcessRunner
{
    public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No external process is expected.");
}

internal sealed class FixedSessionFactory(IProcessingSession session) : IProcessingSessionFactory
{
    public IProcessingSession Create(JobRecord job, AppSettings settings) => session;
}

/// <summary>
/// A worker that records what the processor asked of it, in order. <see cref="EncodeFinished"/> is what it answers when asked
/// whether the encode already finished; a stage it is not asked to run costs nothing.
/// </summary>
internal sealed class ResumedWorkerSession : IProcessingSession
{
    public List<string> Calls { get; } = [];
    public bool EncodeFinished { get; init; }
    public Exception? RetrieveFailure { get; init; }
    public TimeSpan? SearchDuration { get; init; } = TimeSpan.FromSeconds(5);

    public bool IsRemote => true;
    public string ExecutionMode => "remoteSsh";
    public string? RemoteHost => "video-worker";
    public string? RemoteWorkspace => null;

    public Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default)
    {
        Calls.Add("Stage");
        return Task.CompletedTask;
    }

    public Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Calls.Add("Search");
        return Task.FromResult(new CrfSearchResult(42, string.Empty, string.Empty, SearchDuration));
    }

    public Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Calls.Add($"Encode {attempt}");
        return Task.FromResult(new ProcessingStageResult(outputPath, TimeSpan.Zero));
    }

    public Task<bool> HasFinishedEncodeAsync(string outputPath, int attempt, CancellationToken cancellationToken = default)
    {
        Calls.Add($"HasFinishedEncode {attempt}");
        return Task.FromResult(EncodeFinished);
    }

    public async Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default)
    {
        Calls.Add($"Retrieve {attempt}");
        if (RetrieveFailure is not null) throw RetrieveFailure;
        // As the real session does: a resume that skips the encode has not made the folder.
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, "av1", cancellationToken);
    }

    public Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("Cleanup");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Hands each report to the callback on the reporting thread; <see cref="Progress{T}"/> would post it later.</summary>
internal sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>Keeps every line reported, synchronously, unlike <see cref="Progress{T}"/>. Tool output arrives on other threads, so adding is locked.</summary>
internal sealed class ListProgress(List<JobProgress>? lines = null) : IProgress<JobProgress>
{
    public List<JobProgress> Lines { get; } = lines ?? [];

    public void Report(JobProgress value)
    {
        lock (Lines) Lines.Add(value);
    }
}

/// <summary>Settings for a managed Hetzner worker and a job already started on a worker, for the tests of the binding rules.</summary>
internal static class ManagedWorker
{
    public static AppSettings Settings(string databasePath = "") => new()
    {
        Database = new DatabaseSettings { Path = databasePath },
        Processing = new ProcessingSettings
        {
            Mode = ProcessingModes.RemoteSsh,
            RemoteSsh = new RemoteSshSettings
            {
                Lifecycle = RemoteLifecycleModes.Hetzner,
                WorkingDirectory = "/var/tmp/video-optimiser",
                Hetzner = new HetznerSettings { ServerTypes = ["cx43"], Locations = ["hel1", "fsn1"] }
            }
        }
    };

    /// <summary>An encode interrupted on <paramref name="host"/>; with no <paramref name="workspace"/> it is the one these settings give the job.</summary>
    public static JobRecord Started(string directory, string host, string? workspace = null)
    {
        var id = Guid.NewGuid();
        return new JobRecord
        {
            Id = id,
            SourcePath = Path.Combine(directory, "movie.mp4"),
            SourceFingerprint = "source",
            Status = JobStatus.Interrupted,
            ResumeStatus = JobStatus.Encoding,
            Crf = 30,
            Attempt = 1,
            ExecutionMode = ProcessingModes.RemoteSsh,
            RemoteHost = host,
            RemoteWorkspace = workspace ?? $"/var/tmp/video-optimiser/{id:N}"
        };
    }
}
