using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Validate;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Cli;
using VideoOptimiser.Cli.Features.Run;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Features.Run;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// A whole job's console block, from the real ab-av1 logs in <c>Fixtures/AbAv1</c> through <see cref="JobProcessor"/>, the
/// output translator and <see cref="JobProgressConsole"/>. It pins what a user reads, which no single layer's test shows.
/// </summary>
public sealed class JobConsoleOutputTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Console.{Guid.NewGuid():N}");

    public JobConsoleOutputTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ALocalJobReadsAsOneIndentedBlock()
    {
        var source = await CreateSourceAsync("PXL_20250531_041434717.mp4");
        var settings = Settings(remote: false, minimumVmaf: 93);
        var processor = Processor(new ProcessingSessionFactory(_ => new ReplayedCrfSearch(), _ => new ReplayedEncoder(), new UnexpectedExternalProcessRunner()));

        var text = await RunAsync(processor, source, settings);

        text.Should().Be(string.Join('\n',
            "",
            "[1/1] PXL_20250531_041434717.mp4 (19 B)",
            "      Finding the best CRF for VMAF 93 (2 test clips of 20s each)...",
            "        CRF 34   VMAF 92.55   size  46%   below target",
            "        CRF 18   VMAF 97.45   size 137%   passes, file too big",
            "        CRF 33   VMAF 92.81   size  50%   below target",
            "        CRF 32   VMAF 93.44   size  57%   passes, chosen",
            "      Chose CRF 32 in 1m 03s. Expected output: 37.1 MiB (57% of the original).",
            "      Encoding at CRF 32, expected to take about 14 seconds...",
            "      Encoded 49.2 MiB (75% of the original) in 0s.",
            "      Checking the output: streams, duration, savings and decode test...",
            "      Ready to finalise: 1002.4 MiB -> 287.3 MiB, 71.3% saved.",
            ""));
    }

    [Fact]
    public async Task ARemoteJobReadsAsOneIndentedBlockWithoutTheTimeReportOrTimestamps()
    {
        var source = await CreateSourceAsync("PXL_20250531_041434717.mp4");
        var settings = Settings(remote: true, minimumVmaf: 95);
        var processor = Processor(new FixedSessionFactory(new ReplayedRemoteSession()));

        var text = await RunAsync(processor, source, settings);

        text.Should().Be(string.Join('\n',
            "",
            "[1/1] PXL_20250531_041434717.mp4 (19 B)",
            "      Uploading 1002.4 MiB to the worker...",
            "        41% of 1002.4 MiB, 45.2 MiB/s, about 13s left",
            "      Uploaded 1002.4 MiB in 23s, checksum verified.",
            "      Finding the best CRF for VMAF 95 (2 test clips of 20s each)...",
            "        CRF 34   VMAF 95.02   size  19%   passes",
            "        CRF 18   VMAF 99.46   size  61%   passes",
            "        CRF 27   VMAF 97.72   size  33%   passes",
            "        CRF 29   VMAF 97.13   size  29%   passes, chosen",
            "      Chose CRF 29 in 7m 36s. Expected output: 284.9 MiB (29% of the original).",
            "      Encoding at CRF 29, expected to take about 5 minutes...",
            "        20%, 54 fps",
            "      Encoded 99.8 MiB (29% of the original) in 0s.",
            "      Downloading 99.8 MiB from the worker...",
            "      Downloaded 99.8 MiB in 30s, checksum verified.",
            "      Checking the output: streams, duration, savings and decode test...",
            "      Ready to finalise: 1002.4 MiB -> 287.3 MiB, 71.3% saved.",
            ""));
    }

    private static async Task<string> RunAsync(JobProcessor processor, string source, AppSettings settings)
    {
        var output = new StringWriter { NewLine = "\n" };
        var console = new JobProgressConsole(output, TerminalStyle.Plain);
        console.Report(JobProgress.Header(1, 1, source));

        var result = await processor.ProcessAsync(settings.Database.Path, source, settings, force: false, console);

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize, result.Message);
        return output.ToString();
    }

    private async Task<string> CreateSourceAsync(string name)
    {
        var source = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(source, "original video data");
        return source;
    }

    private AppSettings Settings(bool remote, double minimumVmaf) => new()
    {
        Quality = new QualitySettings { MinimumVmaf = minimumVmaf },
        Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
        Savings = new SavingsSettings { MinimumBytesSaved = "0B" },
        Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
        Eligibility = new EligibilitySettings { Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "1Mbps", MinimumFileSize = "1B" }] },
        Processing = remote ? new ProcessingSettings { Mode = "remoteSsh", RemoteSsh = new RemoteSshSettings { Host = "video-worker", WorkingDirectory = "/var/tmp/video-optimiser" } } : new ProcessingSettings()
    };

    private static JobProcessor Processor(IProcessingSessionFactory sessions) => new(
        new SqliteJobRepository(new SqliteDatabaseInitializer()),
        new ReadableFiles(),
        _ => new H264Probe(),
        sessions,
        new FileFingerprintService(),
        new OutputManifestStore(),
        new SizedValidator(),
        new AbAv1ProgressTranslator());

    private static string[] Lines(string fixture) => AbAv1OutputTests.Fixture(fixture).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class ReplayedCrfSearch : ICrfSearchClient
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) => [];

        public Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var lines = Lines("local-crf-search.stderr.log").Concat(Lines("local-crf-search.stdout.log")).ToArray();
            foreach (var line in lines) progress?.Report(JobProgress.Tool(line));
            var output = string.Join('\n', lines);
            var crf = CrfSearchOutputParser.Parse(output);
            return Task.FromResult(new CrfSearchResult(crf, output, string.Empty, TimeSpan.FromSeconds(63), CrfSearchOutputParser.TryParsePrediction(output, crf, out var prediction) ? prediction : null));
        }
    }

    private sealed class ReplayedEncoder : IVideoEncoder
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams) => [];

        public async Task<EncodeResult> EncodeAsync(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            foreach (var line in Lines("local-encode.stderr.log")) progress?.Report(JobProgress.Tool(line));
            await File.WriteAllTextAsync(outputPath, "av1", cancellationToken);
            return new EncodeResult(outputPath, TimeSpan.Zero);
        }
    }

    /// <summary>A remote worker whose ab-av1 stages replay real remote logs and whose transfers report the lines the real session prints.</summary>
    private sealed class ReplayedRemoteSession : IProcessingSession
    {
        public bool IsRemote => true;
        public string ExecutionMode => "remoteSsh";
        public string? RemoteHost => "video-worker";
        public string? RemoteWorkspace => null;

        public Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StageAsync(string sourcePath, string sourceFingerprint, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default)
        {
            progress?.Report(JobProgress.Step("Uploading 1002.4 MiB to the worker..."));
            progress?.Report(JobProgress.Detail("41% of 1002.4 MiB, 45.2 MiB/s, about 13s left"));
            progress?.Report(JobProgress.Step("Uploaded 1002.4 MiB in 23s, checksum verified."));
            return Task.CompletedTask;
        }

        public Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var log = AbAv1OutputTests.Fixture("remote-crf-search.log");
            foreach (var line in Lines("remote-crf-search.log")) progress?.Report(JobProgress.Tool(line));
            var crf = CrfSearchOutputParser.Parse(log);
            return Task.FromResult(new CrfSearchResult(crf, log, string.Empty, TimeSpan.FromSeconds(456), CrfSearchOutputParser.TryParsePrediction(log, crf, out var prediction) ? prediction : null));
        }

        public Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            foreach (var line in Lines("remote-encode.log")) progress?.Report(JobProgress.Tool(line));
            return Task.FromResult(new ProcessingStageResult(outputPath, TimeSpan.Zero));
        }

        public Task<bool> HasFinishedEncodeAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task RetrieveOutputAsync(string outputPath, int attempt, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default)
        {
            progress?.Report(JobProgress.Step("Downloading 99.8 MiB from the worker..."));
            await File.WriteAllTextAsync(outputPath, "av1", cancellationToken);
            progress?.Report(JobProgress.Step("Downloaded 99.8 MiB in 30s, checksum verified."));
        }

        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFinishedWorkspacesAsync(IReadOnlySet<Guid> finishedJobIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SizedValidator : IOutputValidationService
    {
        public Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationReport { Passed = true, SourceSizeBytes = 1_051_100_000, OutputSizeBytes = 301_300_000, PercentageSaved = 71.3m, ValidatedUtc = DateTimeOffset.UtcNow });
    }
}
