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
            new ReadableFiles(),
            _ => new H264Probe(),
            new ProcessingSessionFactory(_ => new FixedCrfSearch(), _ => encoder, new UnexpectedExternalProcessRunner()),
            new FileFingerprintService(),
            new OutputManifestStore(),
            new PassingValidator());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Eligibility = Rules()
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
            new ReadableFiles(),
            _ => new H264Probe(),
            new ProcessingSessionFactory(_ => new CancellingCrfSearch(), _ => new FakeEncoder(), new UnexpectedExternalProcessRunner()),
            new FileFingerprintService(),
            new OutputManifestStore(),
            new PassingValidator());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = databasePath },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Eligibility = Rules()
        };

        var action = async () => await processor.ProcessAsync(databasePath, source, settings, force: false);

        await action.Should().ThrowAsync<OperationCanceledException>();
        (await new SqliteJobRepository(new SqliteDatabaseInitializer()).ListAsync(databasePath, terminal: false)).Should().ContainSingle(job => job.Status == JobStatus.Interrupted);
    }

    [Fact]
    public async Task RemoteConnectionFailureLeavesTheJobInterruptibleAndBoundToItsWorkspace()
    {
        var source = Path.Combine(_directory, "remote.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var databasePath = Path.Combine(_directory, "remote.db");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var processor = new JobProcessor(
            repository,
            new ReadableFiles(),
            _ => new H264Probe(),
            new FixedSessionFactory(new UnavailableRemoteSession()),
            new FileFingerprintService(),
            new OutputManifestStore(),
            new PassingValidator());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = databasePath },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Eligibility = Rules(),
            Processing = new ProcessingSettings { Mode = "remoteSsh", RemoteSsh = new RemoteSshSettings { Host = "video-worker", WorkingDirectory = "/var/tmp/video-optimiser" } }
        };

        var result = await processor.ProcessAsync(databasePath, source, settings, force: false);

        result.Job.Status.Should().Be(JobStatus.Interrupted);
        result.Job.ResumeStatus.Should().Be(JobStatus.Staging);
        result.Job.ExecutionMode.Should().Be("remoteSsh");
        result.Job.RemoteHost.Should().Be("video-worker");
        result.Job.RemoteWorkspace.Should().Be($"/var/tmp/video-optimiser/{result.Job.Id:N}");
    }

    [Theory]
    [InlineData(CancellationStage.Encoding, JobStatus.Queued)]
    [InlineData(CancellationStage.Downloading, JobStatus.Downloading)]
    [InlineData(CancellationStage.Validating, JobStatus.Validating)]
    public async Task RemoteCancellationRestartsEncodingButPreservesLaterResumableStages(CancellationStage cancellationStage, JobStatus expectedResumeStatus)
    {
        var source = Path.Combine(_directory, $"cancel-{cancellationStage}.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var databasePath = Path.Combine(_directory, $"cancel-{cancellationStage}.db");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var session = new CancellingRemoteSession(cancellationStage);
        var processor = new JobProcessor(
            repository,
            new ReadableFiles(),
            _ => new H264Probe(),
            new FixedSessionFactory(session),
            new FileFingerprintService(),
            new OutputManifestStore(),
            cancellationStage == CancellationStage.Validating ? new CancellingValidator() : new PassingValidator());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = databasePath },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Eligibility = Rules(),
            Processing = new ProcessingSettings { Mode = "remoteSsh", RemoteSsh = new RemoteSshSettings { Host = "video-worker", WorkingDirectory = "/var/tmp/video-optimiser" } }
        };

        var action = async () => await processor.ProcessAsync(databasePath, source, settings, force: false);

        await action.Should().ThrowAsync<OperationCanceledException>();
        var job = (await repository.ListAsync(databasePath, terminal: false)).Should().ContainSingle().Subject;
        job.Status.Should().Be(JobStatus.Interrupted);
        job.ResumeStatus.Should().Be(expectedResumeStatus);
        session.CancelCalled.Should().BeTrue();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class ReadableFiles : IFileReadinessService
    {
        public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new FileReadinessResult(true, "Readable."));
    }

    private sealed class H264Probe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new MediaInfo(path.EndsWith(".encoding.mp4", StringComparison.OrdinalIgnoreCase) ? "av1" : "h264", 1, 1, 0, 0, 120, new FileInfo(path).Length, 1920, 1080, 10_000_000));
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

    private sealed class UnexpectedExternalProcessRunner : IExternalProcessRunner
    {
        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("External processes are not expected for local processing tests.");
    }

    private sealed class FixedSessionFactory(IProcessingSession session) : IProcessingSessionFactory
    {
        public IProcessingSession Create(JobRecord job, AppSettings settings) => session;
    }

    private sealed class UnavailableRemoteSession : IProcessingSession
    {
        public bool IsRemote => true;
        public string ExecutionMode => "remoteSsh";
        public string? RemoteHost => "video-worker";
        public string? RemoteWorkspace => null;
        public Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default) => throw new RemoteConnectionException("Remote unavailable.");
        public Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public enum CancellationStage
    {
        Encoding,
        Downloading,
        Validating
    }

    private sealed class CancellingRemoteSession(CancellationStage cancellationStage) : IProcessingSession
    {
        public bool CancelCalled { get; private set; }
        public bool IsRemote => true;
        public string ExecutionMode => "remoteSsh";
        public string? RemoteHost => "video-worker";
        public string? RemoteWorkspace => null;
        public Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new CrfSearchResult(42, string.Empty, string.Empty, TimeSpan.Zero));

        public Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default)
        {
            if (cancellationStage == CancellationStage.Encoding) throw new OperationCanceledException();
            return Task.FromResult(new ProcessingStageResult(outputPath, TimeSpan.Zero));
        }

        public async Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default)
        {
            if (cancellationStage == CancellationStage.Downloading) throw new OperationCanceledException();
            await File.WriteAllTextAsync(outputPath, "av1", cancellationToken);
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            CancelCalled = true;
            return Task.CompletedTask;
        }

        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellingValidator : IOutputValidationService
    {
        public Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default) => throw new OperationCanceledException();
    }

    private static EligibilitySettings Rules() => new EligibilitySettings
    {
        Rules = new List<EligibilityRuleSettings> { new() { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "1Mbps", MinimumFileSize = "1B" } }
    };
}
