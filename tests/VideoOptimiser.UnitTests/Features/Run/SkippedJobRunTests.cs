using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using static VideoOptimiser.UnitTests.RunCli;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// A started video that <c>--local</c> or <c>--remote</c> makes <c>run</c> skip says what would resume it (VOPT-74 B11). The
/// advice used to be "run without --local/--remote" every time, which fails the video as a changed configuration when the
/// config's mode or worker differs from the one the video started on.
/// </summary>
public sealed class SkippedJobRunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    public SkippedJobRunTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    [Fact]
    public async Task WhenTheConfigMatchesTheVideoThePlainRunResumesIt()
    {
        var config = await RunCli.WriteConfigAsync(DatabasePath, ManagedHetzner);
        await StartedOnManagedWorkerAsync();

        var (code, output, _) = await RunAsync("run", "--local", "--config", config);

        code.Should().Be(0);
        Lines(output).Should().Contain($"eligible.mp4 is left for a later run: it started on the Hetzner worker. To resume it: video-optimiser run --config \"{config}\"");
        (await _repository.ListAsync(DatabasePath, terminal: false)).Single().Status.Should().Be(JobStatus.Interrupted);
    }

    /// <summary>The config says this PC, so a plain run would fail the video; the flag that names its own mode resumes it.</summary>
    [Fact]
    public async Task WhenTheConfigUsesAnotherModeTheAdviceNamesTheFlagThatResumesIt()
    {
        var config = await RunCli.WriteConfigAsync(DatabasePath, "processing:", "  mode: \"local\"", "  remoteSsh:", "    lifecycle: \"hetzner\"");
        await StartedOnManagedWorkerAsync();

        var (code, output, _) = await RunAsync("run", "--local", "--config", config);

        code.Should().Be(0);
        Lines(output).Should().Contain($"eligible.mp4 is left for a later run: it started on the Hetzner worker, and your config uses this PC. To resume it: video-optimiser run --remote --config \"{config}\"");
    }

    /// <summary>No flag helps when the config points at another worker: the video's worker is the one to put back, or the video is dropped.</summary>
    [Fact]
    public async Task WhenTheConfigUsesAnotherWorkerTheAdviceSaysToChangeItOrDropTheVideo()
    {
        var config = await RunCli.WriteConfigAsync(DatabasePath, "processing:", "  mode: \"remoteSsh\"", "  remoteSsh:", "    lifecycle: \"manual\"", "    host: \"new-box\"");
        var job = await StartedOnManagedWorkerAsync();

        var (code, output, _) = await RunAsync("run", "--local", "--config", config);

        code.Should().Be(0);
        Lines(output).Should().Contain($"eligible.mp4 is left for a later run: it started on the Hetzner worker, and your config's worker is new-box. To resume it, set processing.remoteSsh back to the worker it started on, or drop it: video-optimiser queue drop {job.Id:N} --config \"{config}\"");
    }

    /// <summary>The config names the video's worker but another working directory, which the host names cannot show.</summary>
    [Fact]
    public async Task WhenOnlyTheWorkingDirectoryDiffersTheAdviceNamesThatSetting()
    {
        var config = await RunCli.WriteConfigAsync(DatabasePath, ManagedHetzner.Concat(["    workingDirectory: \"/srv/other\""]).ToArray());
        var job = await StartedOnManagedWorkerAsync();

        var (code, output, _) = await RunAsync("run", "--local", "--config", config);

        code.Should().Be(0);
        Lines(output).Should().Contain($"eligible.mp4 is left for a later run: it started in another working directory than processing.remoteSsh.workingDirectory. To resume it, set that back, or drop it: video-optimiser queue drop {job.Id:N} --config \"{config}\"");
    }

    /// <summary>A manual worker is named by its host, and the command keeps the typed <c>--config</c>.</summary>
    [Fact]
    public async Task AManualWorkerIsNamedByItsHost()
    {
        var config = await RunCli.WriteConfigAsync(DatabasePath, "processing:", "  mode: \"remoteSsh\"", "  remoteSsh:", "    lifecycle: \"manual\"", "    host: \"old-box\"");
        await StartedOnManagedWorkerAsync("old-box");

        var (code, output, _) = await RunAsync("run", "--local", "--config", config);

        code.Should().Be(0);
        Lines(output).Should().Contain($"eligible.mp4 is left for a later run: it started on the worker old-box. To resume it: video-optimiser run --config \"{config}\"");
    }

    private async Task<JobRecord> StartedOnManagedWorkerAsync(string host = RemoteExecutionIdentity.ManagedHetzner)
    {
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "eligible.mp4"), Status = JobStatus.Queued, ExecutionMode = ProcessingModes.RemoteSsh, RemoteHost = host };
        job.RemoteWorkspace = $"/var/tmp/video-optimiser/{job.Id:N}";
        job = await _repository.CreateAsync(DatabasePath, job);
        (job.Status, job.ResumeStatus, job.Crf) = (JobStatus.Interrupted, JobStatus.Encoding, 30);
        await _repository.UpdateAsync(DatabasePath, job);
        return job;
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) =>
        ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            CliFakes.NotInteractive(services);
            services.AddSingleton<IRemoteWorkerLifecycle>(new FakeWorkerLifecycle { Exists = false });
            services.AddSingleton<IJobProcessor>(new ScriptedRunProcessor(remote: true));
        }, args);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
