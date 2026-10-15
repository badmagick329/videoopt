using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.Validate;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using static VideoOptimiser.UnitTests.RunCli;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// An original that vanishes, or cannot be read, between the run's exists check and its fingerprint fails that video only
/// (VOPT-74 B21). It used to end the whole run as a configuration error (exit 3) and leave the videos after it unconverted.
/// The real CLI runs the real processor with a fingerprint service that fails for <c>a.mp4</c>.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class UnreadableOriginalRunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    public UnreadableOriginalRunTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    [Theory]
    [InlineData(true, "SourceMissing", "Original does not exist: {0}")]
    [InlineData(false, "ProcessingFailed", "disk read failed")]
    public async Task TheVideoFailsAndTheRunConvertsTheRest(bool vanished, string category, string message)
    {
        var config = await RunCli.WriteConfigAsync(DatabasePath, "watch:", "  roots:", $"    - path: '{_directory}'", "eligibility:", "  rules:", "    - codecs: ['h264']", "      resolution: '1080p-1440p'", "      minimumVideoBitrate: '1Mbps'", "      minimumFileSize: '1B'", "savings:", "  minimumBytesSaved: '0B'");
        var jobs = await QueueAsync("a.mp4", "b.mp4");
        var first = Path.Combine(_directory, "a.mp4");
        Exception failure = vanished ? new FileNotFoundException("Original does not exist.", first) : new IOException("disk read failed");

        var (code, output, error) = await ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            services.AddSingleton<IFileFingerprintService>(new FailingFor(first, failure));
            services.AddSingleton<Func<string, IMediaProbe>>(_ => new H264Probe());
            services.AddSingleton<IFileReadinessService>(new ReadableFiles());
            services.AddSingleton<IOutputValidationService>(new PassingValidator());
            services.AddSingleton<Func<string, ICrfSearchClient>>(_ => _ => new FixedCrfSearch());
            services.AddSingleton<Func<string, IVideoEncoder>>(_ => _ => new FakeEncoder());
        }, "run", "--config", config);

        var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, message, first);
        error.Should().BeEmpty();
        code.Should().Be((int)ExitCode.PartialSuccess);
        output.Should().Be(Transcript(
            "Encoding on this PC.",
            "Converting 2 videos.",
            "",
            "[1/2] a.mp4 (19 B)",
            $"      Failed: {text}",
            "",
            "[2/2] b.mp4 (19 B)",
            "      Finding the best CRF for VMAF 95 (2 test clips of 20s each)...",
            "      Chose CRF 42 in 0s.",
            "      Encoding at CRF 42...",
            "      Checking the output: streams, duration, savings and decode test...",
            "      Ready to finalise: 100 B -> 50 B, 50.0% saved.",
            "Ready to finalise: 1. Failed: 1.",
            "",
            "Failed jobs:",
            $"{jobs[0].Id:N}  a.mp4",
            category == "SourceMissing" ? text : $"Failed: {text}",
            $"1 video is ready to finalise: review it, then run 'video-optimiser finalize --ready --config \"{config}\"'."));
        (await StatusesAsync(_repository, DatabasePath, jobs)).Should().Equal(JobStatus.Failed, JobStatus.ReadyToFinalize);
    }

    private async Task<JobRecord[]> QueueAsync(params string[] names)
    {
        var jobs = new List<JobRecord>();
        foreach (var name in names)
        {
            var path = Path.Combine(_directory, name);
            await File.WriteAllTextAsync(path, "original video data");
            jobs.Add(await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = path, Status = JobStatus.Queued }));
            // Run order is by creation time.
            await Task.Delay(5);
        }
        return [.. jobs];
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class FailingFor(string path, Exception failure) : IFileFingerprintService
    {
        public Task<string> CreateAsync(string candidate, CancellationToken cancellationToken = default) =>
            string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase) ? throw failure : Task.FromResult("fingerprint");
    }
}
