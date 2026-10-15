using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using static VideoOptimiser.UnitTests.RunCli;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// What <c>run</c> prints when the worker stops answering (VOPT-69, wording U2, U3, U3m). The real CLI
/// runs with a fake worker and a processor that reports the first job interrupted by an unreachable worker, at the encode stage.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class UnreachableWorkerRunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeWorkerLifecycle _lifecycle = new();
    private readonly ScriptedRunProcessor _processor = new(remote: true) { Unreachable = { [0] = true } };

    public UnreachableWorkerRunTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    [Fact]
    public async Task AQueueRunStopsAtTheFirstJobAndSaysTheManagedWorkerBillsUntilDeleted()
    {
        var config = await WriteConfigAsync(managed: true);
        var jobs = await QueueJobsAsync(3);

        var (code, output, _) = await RunAsync("run", "--config", config);

        _processor.Calls.Should().Be(1);
        Occurrences(output, "can't be reached").Should().Be(1);
        Lines(output).Should().ContainInOrder(
            "Stopped: the worker video-optimiser-1 can't be reached, so 3 videos were not converted.",
            "Nothing to finalise.",
            "The worker stays up, billed hourly, until it is deleted.",
            $"To resume: video-optimiser run --config \"{config}\"",
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

        var (code, output, _) = await RunAsync("run", "--config", config);

        Lines(output).Should().ContainInOrder(
            "Stopped: the worker my-host can't be reached, so 2 videos were not converted.",
            $"To resume: video-optimiser run --config \"{config}\"");
        output.Should().NotContain("billed hourly").And.NotContain("Remote worker lifecycle is manual");
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

    private Task<JobRecord[]> QueueJobsAsync(int count) => RunCli.QueueJobsAsync(_repository, DatabasePath, count);

    private Task<string> WriteConfigAsync(bool managed) => RunCli.WriteConfigAsync(DatabasePath,
        managed ? RunCli.ManagedHetzner : ["processing:", "  mode: \"remoteSsh\"", "  remoteSsh:", "    lifecycle: \"manual\"", "    host: \"my-host\""]);

    private static int Occurrences(string text, string value) => text.Split(value).Length - 1;

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
