using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Jobs;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// What <c>queue run</c> and <c>process</c> print when the worker stops answering (VOPT-69, wording U2, U3, U3m). The real CLI
/// runs with a fake worker and a processor that reports the first job interrupted by an unreachable worker.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class UnreachableWorkerRunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeWorkerLifecycle _lifecycle = new();
    private readonly UnreachableProcessor _processor = new();

    public UnreachableWorkerRunTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    [Fact]
    public async Task AQueueRunStopsAtTheFirstJobAndSaysTheManagedWorkerBillsUntilDeleted()
    {
        var config = await WriteConfigAsync(managed: true);
        var jobs = await QueueJobsAsync(3);

        var (code, output, _) = await RunAsync("queue", "run", "--config", config);

        _processor.Calls.Should().Be(1);
        Occurrences(output, "can't be reached").Should().Be(1);
        Lines(output).Should().ContainInOrder(
            "Stopped: the worker video-optimiser-1 can't be reached, so 3 videos were not converted.",
            "Nothing to finalise.",
            "The worker stays up, billed hourly, until it is deleted.",
            $"To resume: video-optimiser queue run --config \"{config}\"",
            $"To delete it instead: video-optimiser worker delete --config \"{config}\" (the work in progress is lost)");
        output.Should().NotContain("Failed jobs:");
        code.Should().Be((int)ExitCode.ProcessingFailure);
        _lifecycle.DeleteCalls.Should().Be(0);
        var stored = await _repository.ListAsync(DatabasePath, terminal: false);
        stored.Single(job => job.Id == jobs[0].Id).Status.Should().Be(JobStatus.Interrupted);
        stored.Where(job => job.Id != jobs[0].Id).Should().OnlyContain(job => job.Status == JobStatus.Queued);
    }

    [Fact]
    public async Task AManualHostHearsOnlyHowToResume()
    {
        var config = await WriteConfigAsync(managed: false);
        await QueueJobsAsync(2);

        var (code, output, _) = await RunAsync("queue", "run", "--config", config);

        Lines(output).Should().ContainInOrder(
            "Stopped: the worker my-host can't be reached, so 2 videos were not converted.",
            $"To resume: video-optimiser queue run --config \"{config}\"");
        output.Should().NotContain("billed hourly").And.NotContain("Remote worker lifecycle is manual");
        code.Should().Be((int)ExitCode.ProcessingFailure);
    }

    /// <summary><c>process</c> has one video, whose outcome line already says the worker is unreachable.</summary>
    [Fact]
    public async Task ProcessPrintsNoStopLineAndNoFailedJob()
    {
        var config = await WriteConfigAsync(managed: true);
        var source = Path.Combine(_directory, "clip.mp4");
        await File.WriteAllBytesAsync(source, new byte[16]);

        var (code, output, _) = await RunAsync("process", source, "--config", config);

        output.Should().NotContain("can't be reached").And.NotContain("Failed jobs:");
        Lines(output).Should().ContainInOrder(
            "The worker stays up, billed hourly, until it is deleted.",
            $"To resume: video-optimiser process \"{source}\" --config \"{config}\"");
        code.Should().Be((int)ExitCode.ProcessingFailure);
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) =>
        ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            CliFakes.NotInteractive(services);
            services.AddSingleton<IRemoteWorkerLifecycle>(_lifecycle);
            services.AddSingleton<IJobProcessor>(_processor);
        }, args);

    private async Task<JobRecord[]> QueueJobsAsync(int count)
    {
        var jobs = new List<JobRecord>();
        for (var index = 1; index <= count; index++)
        {
            jobs.Add(await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, $"{index}.mp4"), Status = JobStatus.Queued }));
            // Run order is by creation time.
            await Task.Delay(5);
        }
        return [.. jobs];
    }

    private async Task<string> WriteConfigAsync(bool managed)
    {
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, string.Join('\n',
            "database:",
            $"  path: '{DatabasePath}'",
            "processing:",
            "  mode: \"remoteSsh\"",
            "  remoteSsh:",
            managed ? "    lifecycle: \"hetzner\"" : "    lifecycle: \"manual\"",
            managed ? "" : "    host: \"my-host\"",
            ""));
        return config;
    }

    private static string[] Lines(string output) => output.Split(Environment.NewLine);

    private static int Occurrences(string text, string value) => text.Split(value).Length - 1;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>Records the job interrupted by an unreachable worker, as <see cref="JobProcessor"/> does, at the encode stage.</summary>
    private sealed class UnreachableProcessor : IJobProcessor
    {
        public Task<bool> NeedsWorkerAsync(string databasePath, string sourcePath, AppSettings settings, bool force, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public int Calls { get; private set; }

        public async Task<JobProcessingResult> ProcessAsync(string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
            var job = await repository.FindOpenBySourceAsync(databasePath, Path.GetFullPath(sourcePath), cancellationToken)
                ?? await repository.CreateAsync(databasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.GetFullPath(sourcePath), Status = JobStatus.Queued }, cancellationToken);
            job.Status = JobStatus.Interrupted;
            job.ResumeStatus = JobStatus.Encoding;
            job.FailureCategory = JobProcessor.RemoteUnavailableCategory;
            job.FailureMessage = "SSH connection failed: timed out.";
            await repository.UpdateAsync(databasePath, job, cancellationToken);
            progress?.Report(JobProgress.Outcome(JobTone.Failure, "Worker unreachable: SSH connection failed: timed out."));
            return new JobProcessingResult(job, ExitCode.ProcessingFailure, job.FailureMessage);
        }
    }
}
