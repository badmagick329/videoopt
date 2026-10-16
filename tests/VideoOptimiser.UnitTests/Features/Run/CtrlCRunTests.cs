using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.ImmichRun;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli.Features.Run;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using static VideoOptimiser.UnitTests.RunCli;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// Two-press Ctrl+C on a run on this PC (VOPT-54 step 3, approved wording). The real CLI runs with a processor that presses
/// Ctrl+C partway through a video, as <see cref="RunStop"/> hears it from the console.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class CtrlCRunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly RunStop _stop = new();
    private readonly ScriptedRunProcessor _processor = new();

    public CtrlCRunTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    /// <summary>
    /// The estimate is the encode's: once a later step starts, the line says nothing about time. A stop after a video on this
    /// PC ends with how to convert the rest, and nothing about a worker.
    /// </summary>
    [Fact]
    public async Task AFirstCtrlCAfterTheEncodeGivesNoTimeLeft()
    {
        var config = await WriteConfigAsync(DatabasePath);
        var jobs = await QueueJobsAsync(_repository, DatabasePath, 2);
        _processor.During[0] = progress =>
        {
            progress!.Report(JobProgress.Step("Checking the AV1."));
            _stop.Press();
            return Task.CompletedTask;
        };

        var (code, output, _) = await RunAsync("run", "--config", config);

        output.Should().Be(Transcript(
            "Encoding on this PC.",
            "Converting 2 videos.",
            "",
            "[1/2] 1.mp4",
            "      Encoding.",
            "        41%, 63 fps, about 7m 36s left",
            "      Checking the AV1.",
            "Stopping after this video. Press Ctrl+C again to stop now.",
            "      Ready to finalise.",
            "",
            "Stopped after 1.mp4; 1 video is still waiting.",
            "Ready to finalise: 1.",
            $"1 video is ready to finalise: review it, then run 'video-optimiser finalize --ready --config \"{config}\"'.",
            $"To convert the rest: video-optimiser run --config \"{config}\""));
        code.Should().Be((int)ExitCode.Cancelled);
        _processor.Calls.Should().Be(1);
        (await StatusesAsync(_repository, DatabasePath, jobs)).Should().Equal(JobStatus.ReadyToFinalize, JobStatus.Queued);
    }

    /// <summary>A first press on the last video changes nothing about how the run ends: there is nothing left to stop before.</summary>
    [Fact]
    public async Task AFirstCtrlCOnTheLastVideoEndsTheRunAsFinished()
    {
        var config = await WriteConfigAsync(DatabasePath);
        await QueueJobsAsync(_repository, DatabasePath, 1);
        PressDuringTheFirstVideo(1);

        var (code, output, _) = await RunAsync("run", "--config", config);

        output.Should().Be(Transcript(
            "Encoding on this PC.",
            "Converting 1 video.",
            "",
            "[1/1] 1.mp4",
            "      Encoding.",
            "        41%, 63 fps, about 7m 36s left",
            "Stopping after this video (the encode has about 7m 36s left). Press Ctrl+C again to stop now.",
            "      Ready to finalise.",
            "Ready to finalise: 1.",
            $"1 video is ready to finalise: review it, then run 'video-optimiser finalize --ready --config \"{config}\"'."));
        code.Should().Be((int)ExitCode.Success);
    }

    [Fact]
    public async Task ASecondCtrlCStopsNowAndSaysTheVideoContinuesOnTheNextRun()
    {
        var config = await WriteConfigAsync(DatabasePath);
        var jobs = await QueueJobsAsync(_repository, DatabasePath, 3);
        PressDuringTheFirstVideo(2);

        var (code, output, _) = await RunAsync("run", "--config", config);

        output.Should().Be(Transcript(
            "Encoding on this PC.",
            "Converting 3 videos.",
            "",
            "[1/3] 1.mp4",
            "      Encoding.",
            "        41%, 63 fps, about 7m 36s left",
            "Stopping after this video (the encode has about 7m 36s left). Press Ctrl+C again to stop now.",
            "",
            "Stopped now. 1.mp4 continues from its last finished step on the next run.",
            $"To resume: video-optimiser run --config \"{config}\""));
        code.Should().Be((int)ExitCode.Cancelled);
        var stored = await _repository.GetAsync(DatabasePath, jobs[0].Id);
        stored!.Status.Should().Be(JobStatus.Interrupted);
        stored.ResumeStatus.Should().Be(JobStatus.Encoding);
        (await StatusesAsync(_repository, DatabasePath, jobs)).Skip(1).Should().OnlyContain(status => status == JobStatus.Queued);
    }

    /// <summary>With no video converting there is nothing to finish first, so the first press stops now.</summary>
    [Fact]
    public async Task ACtrlCDuringImmichDiscoveryStopsNow()
    {
        var config = await WriteConfigAsync(DatabasePath, "immich:", "  baseUrl: 'http://immich.invalid'");

        var (code, output, _) = await ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            services.AddSingleton(_stop);
            services.AddSingleton<IImmichRunService>(new PressingDiscovery(_stop));
        }, "immich", "run", "--config", config);

        output.Should().Be(Transcript(
            "Encoding on this PC.",
            "",
            "Stopped now.",
            $"To resume: video-optimiser run --config \"{config}\""));
        code.Should().Be((int)ExitCode.Cancelled);
    }

    private void PressDuringTheFirstVideo(int presses) => _processor.During[0] = _ =>
    {
        for (var press = 0; press < presses; press++) _stop.Press();
        return Task.CompletedTask;
    };

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) =>
        ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            services.AddSingleton(_stop);
            services.AddSingleton<IJobProcessor>(_processor);
        }, args);

    public void Dispose()
    {
        _stop.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Ctrl+C during Immich discovery, which observes the run's token as the real service does.</summary>
    private sealed class PressingDiscovery(RunStop stop) : IImmichRunService
    {
        public Task<ImmichQueueAddition> AddToQueueAsync(string databasePath, AppSettings settings, int? limit, IProgress<ScanProgress>? guardProgress = null, IProgress<ImmichProgress>? discoveryProgress = null, CancellationToken cancellationToken = default)
        {
            stop.Press();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The press should have stopped discovery.");
        }
    }
}
