using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Jobs;

namespace VideoOptimiser.UnitTests;

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
    private readonly PressingProcessor _processor;

    public CtrlCRunTests()
    {
        Directory.CreateDirectory(_directory);
        _processor = new PressingProcessor(_repository, _stop);
    }

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    [Fact]
    public async Task TheFirstCtrlCStopsAfterThisVideoWithTheEncodesTimeLeftAndLeavesTheRestWaiting()
    {
        var config = await WriteConfigAsync();
        var jobs = await QueueJobsAsync(3);
        _processor.PressDuring[0] = 1;

        var (code, output, _) = await RunAsync("queue", "run", "--config", config);

        output.Should().Be(Transcript(
            "Encoding on this PC.",
            "Converting 3 videos.",
            "",
            "[1/3] 1.mp4",
            "      Encoding...",
            "        41%, 63 fps, about 7m 36s left",
            "Stopping after this video (the encode has about 7m 36s left). Press Ctrl+C again to stop now.",
            "      Ready to finalise.",
            "",
            "Stopped after 1.mp4; 2 videos are still waiting.",
            "Ready to finalise: 1.",
            "Next: review the outputs, then run 'video-optimiser finalize --ready'.",
            $"To convert the rest: video-optimiser queue run --config \"{config}\""));
        code.Should().Be((int)ExitCode.Cancelled);
        _processor.Calls.Should().Be(1);
        (await StatusesAsync(jobs)).Should().Equal(JobStatus.ReadyToFinalize, JobStatus.Queued, JobStatus.Queued);
    }

    /// <summary>The estimate is the encode's: once a later step starts, the line says nothing about time.</summary>
    [Fact]
    public async Task AFirstCtrlCAfterTheEncodeGivesNoTimeLeft()
    {
        var config = await WriteConfigAsync();
        await QueueJobsAsync(2);
        _processor.PressDuring[0] = 1;
        _processor.StepBeforePress = "Checking the AV1...";

        var (_, output, _) = await RunAsync("queue", "run", "--config", config);

        Lines(output).Should().ContainInOrder(
            "      Checking the AV1...",
            "Stopping after this video. Press Ctrl+C again to stop now.",
            "      Ready to finalise.");
        output.Should().NotContain("left)");
    }

    /// <summary>A first press on the last video changes nothing about how the run ends: there is nothing left to stop before.</summary>
    [Fact]
    public async Task AFirstCtrlCOnTheLastVideoEndsTheRunAsFinished()
    {
        var config = await WriteConfigAsync();
        await QueueJobsAsync(1);
        _processor.PressDuring[0] = 1;

        var (code, output, _) = await RunAsync("queue", "run", "--config", config);

        output.Should().Be(Transcript(
            "Encoding on this PC.",
            "Converting 1 video.",
            "",
            "[1/1] 1.mp4",
            "      Encoding...",
            "        41%, 63 fps, about 7m 36s left",
            "Stopping after this video (the encode has about 7m 36s left). Press Ctrl+C again to stop now.",
            "      Ready to finalise.",
            "Ready to finalise: 1.",
            "Next: review the outputs, then run 'video-optimiser finalize --ready'."));
        code.Should().Be((int)ExitCode.Success);
    }

    [Fact]
    public async Task ASecondCtrlCStopsNowAndSaysTheVideoContinuesOnTheNextRun()
    {
        var config = await WriteConfigAsync();
        var jobs = await QueueJobsAsync(3);
        _processor.PressDuring[0] = 2;

        var (code, output, _) = await RunAsync("queue", "run", "--config", config);

        output.Should().Be(Transcript(
            "Encoding on this PC.",
            "Converting 3 videos.",
            "",
            "[1/3] 1.mp4",
            "      Encoding...",
            "        41%, 63 fps, about 7m 36s left",
            "Stopping after this video (the encode has about 7m 36s left). Press Ctrl+C again to stop now.",
            "",
            "Stopped now. 1.mp4 continues from its last finished step on the next run.",
            $"To resume: video-optimiser queue run --config \"{config}\""));
        code.Should().Be((int)ExitCode.Cancelled);
        var stored = await _repository.GetAsync(DatabasePath, jobs[0].Id);
        stored!.Status.Should().Be(JobStatus.Interrupted);
        stored.ResumeStatus.Should().Be(JobStatus.Encoding);
        (await StatusesAsync(jobs)).Skip(1).Should().OnlyContain(status => status == JobStatus.Queued);
    }

    /// <summary>With no video converting there is nothing to finish first, so the first press stops now.</summary>
    [Fact]
    public async Task ACtrlCDuringImmichDiscoveryStopsNow()
    {
        var config = await WriteConfigAsync(immich: true);

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
            $"To resume: video-optimiser immich run --config \"{config}\""));
        code.Should().Be((int)ExitCode.Cancelled);
    }

    [Fact]
    public void ACtrlCWhileDeletingTheWorkerStopsNothing()
    {
        var presses = 0;
        using (_stop.WhileDeleting(() => presses++))
        {
            _stop.Press();
            _stop.Press();
        }

        presses.Should().Be(2);
        _stop.Now.IsCancellationRequested.Should().BeFalse();
        _stop.Press();
        _stop.Now.IsCancellationRequested.Should().BeTrue();
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) =>
        ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            services.AddSingleton(_stop);
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

    private async Task<JobStatus[]> StatusesAsync(IEnumerable<JobRecord> jobs)
    {
        var statuses = new List<JobStatus>();
        foreach (var job in jobs) statuses.Add((await _repository.GetAsync(DatabasePath, job.Id))!.Status);
        return [.. statuses];
    }

    private async Task<string> WriteConfigAsync(bool immich = false)
    {
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, string.Join('\n',
            "database:",
            $"  path: '{DatabasePath}'",
            immich ? "immich:\n  baseUrl: 'http://immich.invalid'" : "",
            ""));
        return config;
    }

    private static string Transcript(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    private static string[] Lines(string output) => output.Split(Environment.NewLine);

    public void Dispose()
    {
        _stop.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// Converts each video through the statuses <see cref="JobProcessor"/> writes, pressing Ctrl+C partway as many times as
    /// <see cref="PressDuring"/> says, and stops as <see cref="JobProcessor"/> does when the run's token is cancelled.
    /// </summary>
    private sealed class PressingProcessor(SqliteJobRepository repository, RunStop stop) : IJobProcessor
    {
        public Dictionary<int, int> PressDuring { get; } = [];
        public string? StepBeforePress { get; set; }
        public int Calls { get; private set; }

        public Task<bool> NeedsWorkerAsync(string databasePath, string sourcePath, AppSettings settings, bool force, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public async Task<JobProcessingResult> ProcessAsync(string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var index = Calls++;
            var job = (await repository.FindOpenBySourceAsync(databasePath, Path.GetFullPath(sourcePath), CancellationToken.None))!;
            job.Status = JobStatus.Encoding;
            await repository.UpdateAsync(databasePath, job, CancellationToken.None);
            progress?.Report(JobProgress.Step("Encoding..."));
            progress?.Report(JobProgress.Detail("41%, 63 fps, about 7m 36s left") with { TimeLeft = new TimeSpan(0, 7, 36) });
            if (StepBeforePress is not null) progress?.Report(JobProgress.Step(StepBeforePress));
            for (var press = 0; press < PressDuring.GetValueOrDefault(index); press++) stop.Press();
            if (cancellationToken.IsCancellationRequested)
            {
                job.Status = JobStatus.Interrupted;
                job.ResumeStatus = JobStatus.Encoding;
                await repository.UpdateAsync(databasePath, job, CancellationToken.None);
                throw new JobInterruptedException(job, JobStatus.Encoding, remote: false, cancellationToken);
            }

            job.Status = JobStatus.Validating;
            await repository.UpdateAsync(databasePath, job, CancellationToken.None);
            job.Status = JobStatus.ReadyToFinalize;
            await repository.UpdateAsync(databasePath, job, CancellationToken.None);
            progress?.Report(JobProgress.Outcome(JobTone.Success, "Ready to finalise."));
            return new JobProcessingResult(job, ExitCode.Success, "Ready to finalise.");
        }
    }

    /// <summary>Ctrl+C during Immich discovery, which observes the run's token as the real service does.</summary>
    private sealed class PressingDiscovery(RunStop stop) : IImmichRunService
    {
        public Task<ImmichRunPreparation> PrepareAsync(string databasePath, AppSettings settings, int? limit, IProgress<ScanProgress>? guardProgress = null, IProgress<ImmichDiscoveryProgress>? discoveryProgress = null, bool modeOverridden = false, CancellationToken cancellationToken = default)
        {
            stop.Press();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The press should have stopped discovery.");
        }
    }
}
