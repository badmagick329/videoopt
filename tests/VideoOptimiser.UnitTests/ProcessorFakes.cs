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

internal sealed class ReadableFiles : IFileReadinessService
{
    public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new FileReadinessResult(true, "Readable."));
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

/// <summary>Keeps every line reported, synchronously, unlike <see cref="Progress{T}"/>.</summary>
internal sealed class ListProgress(List<JobProgress>? lines = null) : IProgress<JobProgress>
{
    public List<JobProgress> Lines { get; } = lines ?? [];

    public void Report(JobProgress value) => Lines.Add(value);
}
