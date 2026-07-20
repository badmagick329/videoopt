using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class JobProcessorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public JobProcessorTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ProcessAsyncPersistsAReadyToFinalizeJobAndReusesIt()
    {
        var source = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var encoder = new FakeEncoder();
        var processor = new JobProcessor(
            repository,
            new StableFiles(),
            _ => new H264Probe(),
            _ => new FixedCrfSearch(),
            _ => encoder,
            new FileFingerprintService(),
            new OutputManifestStore(),
            new PassingValidator());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Processing = new ProcessingSettings { MinimumFileSize = "1B" }
        };

        var first = await processor.ProcessAsync(settings.Database.Path, source, settings, force: false);
        var second = await processor.ProcessAsync(settings.Database.Path, source, settings, force: false);

        first.ExitCode.Should().Be(ExitCode.Success);
        first.Job.Status.Should().Be(JobStatus.ReadyToFinalize);
        first.Job.Crf.Should().Be(42);
        first.Job.ValidationPassed.Should().BeTrue();
        File.Exists(first.Job.OutputPath).Should().BeTrue();
        second.Job.Id.Should().Be(first.Job.Id);
        encoder.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessAsyncMarksTheJobInterruptedWhenCancelled()
    {
        var source = Path.Combine(_directory, "cancelled.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var databasePath = Path.Combine(_directory, "cancelled.db");
        var processor = new JobProcessor(
            new SqliteJobRepository(new SqliteDatabaseInitializer()),
            new StableFiles(),
            _ => new H264Probe(),
            _ => new CancellingCrfSearch(),
            _ => new FakeEncoder(),
            new FileFingerprintService(),
            new OutputManifestStore(),
            new PassingValidator());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = databasePath },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Processing = new ProcessingSettings { MinimumFileSize = "1B" }
        };

        var action = async () => await processor.ProcessAsync(databasePath, source, settings, force: false);

        await action.Should().ThrowAsync<OperationCanceledException>();
        (await new SqliteJobRepository(new SqliteDatabaseInitializer()).ListAsync(databasePath, terminal: true)).Should().ContainSingle(job => job.Status == JobStatus.Interrupted);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class StableFiles : IFileStabilityService
    {
        public Task<StabilityResult> WaitUntilStableAsync(string path, StabilitySettings settings, bool requireRepeatedObservations = true, CancellationToken cancellationToken = default) => Task.FromResult(new StabilityResult(true, "Stable."));
    }

    private sealed class H264Probe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new MediaInfo(path.EndsWith(".encoding.mp4", StringComparison.OrdinalIgnoreCase) ? "av1" : "h264", 1, 1, 0, 0, 120, new FileInfo(path).Length));
    }

    private sealed class FixedCrfSearch : ICrfSearchClient
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) => [];
        public Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new CrfSearchResult(42, string.Empty, string.Empty, TimeSpan.Zero));
    }

    private sealed class CancellingCrfSearch : ICrfSearchClient
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) => [];
        public Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default) => throw new OperationCanceledException();
    }

    private sealed class FakeEncoder : IVideoEncoder
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int crf, QualitySettings settings) => [];
        public async Task<EncodeResult> EncodeAsync(string inputPath, string outputPath, int crf, QualitySettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            await File.WriteAllTextAsync(outputPath, "av1", cancellationToken);
            return new EncodeResult(outputPath, TimeSpan.Zero);
        }
    }

    private sealed class PassingValidator : IOutputValidationService
    {
        public Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(new ValidationReport { Passed = true, SourceSizeBytes = 100, OutputSizeBytes = 50, PercentageSaved = 50, ValidatedUtc = DateTimeOffset.UtcNow });
    }
}
