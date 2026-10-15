using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests.Features.Queue;

/// <summary>
/// <c>queue hold</c>, <c>release</c>, <c>next</c> and <c>drop</c> through the real CLI (VOPT-54 step 5): every approved line,
/// whole, with its exit code, and what each leaves in the database. A run is "going" when its lock is held, which these tests
/// do in-process with <see cref="DatabaseLock"/>.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class QueueVerbTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeImmich _immich = new();
    private string _config = string.Empty;

    public QueueVerbTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");
    private string StagingDirectory => Path.Combine(_directory, "staging");
    private string ConfigArgument => $" --config \"{_config}\"";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HoldParksAWaitingOrStoppedVideoAndKeepsItsResumePoint(bool stopped)
    {
        var job = await QueuedAsync("clip.mp4");
        if (stopped) await StopAsync(job, JobStatus.Encoding);

        var result = await RunAsync("queue", "hold", job.Id.ToString("N"));

        result.Should().Be((0, Lines($"Held clip.mp4. Runs pass over it until 'video-optimiser queue release {job.Id:N}{ConfigArgument}'."), ""));
        var stored = await StoredAsync(job);
        stored.Held.Should().BeTrue();
        stored.ResumeStatus.Should().Be(stopped ? JobStatus.Encoding : null);
    }

    [Fact]
    public async Task HoldOnAHeldVideoSaysSo()
    {
        var job = await HeldAsync(await QueuedAsync("clip.mp4"));

        (await RunAsync("queue", "hold", job.Id.ToString("N"))).Should().Be((0, Lines("clip.mp4 is already held."), ""));
    }

    [Theory]
    [InlineData(true, "Released clip.mp4; runs take it again.")]
    [InlineData(false, "clip.mp4 isn't held.")]
    public async Task ReleaseTakesAHeldVideoBack(bool held, string line)
    {
        var job = await QueuedAsync("clip.mp4");
        if (held) await HeldAsync(job);

        (await RunAsync("queue", "release", job.Id.ToString("N"))).Should().Be((0, Lines(line), ""));
        (await StoredAsync(job)).Held.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, null, "clip.mp4 runs next.")]
    [InlineData(true, null, "clip.mp4 runs next; it's no longer held.")]
    [InlineData(false, "video-optimiser immich run", "clip.mp4 is now first in the queue.")]
    [InlineData(true, "video-optimiser immich run", "clip.mp4 is now first in the queue; it's no longer held.")]
    [InlineData(false, "video-optimiser run", "clip.mp4 runs next.")]
    [InlineData(false, "video-optimiser run", "IMG_0001.MOV runs next.")]
    [InlineData(false, "queue run", "clip.mp4 is now first in the queue.")]
    [InlineData(false, "queue drop", "clip.mp4 runs next.")]
    public async Task NextPutsAVideoFirstAndSaysWhetherTheRunGoingNowTakesIt(bool held, string? runGoing, string line)
    {
        var earlier = await QueuedAsync("earlier.mp4");
        var job = line.StartsWith("IMG", StringComparison.Ordinal) ? await ImmichQueuedAsync() : await QueuedAsync("clip.mp4");
        if (held) await HeldAsync(job);
        using var run = runGoing is null ? null : DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), runGoing, out _);

        var result = await RunAsync("queue", "next", job.Id.ToString("N"));

        result.Should().Be((0, Lines(line), ""));
        var stored = await StoredAsync(job);
        stored.Held.Should().BeFalse();
        JobRunOrder.Sort([stored, await StoredAsync(earlier)]).First().Id.Should().Be(job.Id);
    }

    [Fact]
    public async Task NextOnAVideoConvertingNowSaysSoAndChangesNothing()
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Encoding);
        using var run = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), "video-optimiser run", out _);

        (await RunAsync("queue", "next", job.Id.ToString("N"))).Should().Be((0, Lines("clip.mp4 is converting now."), ""));
        (await StoredAsync(job)).Priority.Should().Be(0);
    }

    [Theory]
    [InlineData("hold", "To hold it")]
    [InlineData("drop", "To drop it")]
    public async Task AVideoConvertingNowIsNotHeldOrDropped(string verb, string advice)
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Encoding);
        using var run = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), "video-optimiser run", out _);

        (await RunAsync("queue", verb, job.Id.ToString("N"))).Should().Be((1, "", Lines($"clip.mp4 is converting now (encoding). {advice}, press Ctrl+C twice in the run's window first.")));
        (await StoredAsync(job)).Status.Should().Be(JobStatus.Encoding);
    }

    /// <summary>R9: with no run holding the lock, a video in a converting status was left by a crash; it is stopped first, keeping its stage.</summary>
    [Theory]
    [InlineData("hold", "Held clip.mp4. Runs pass over it until 'video-optimiser queue release {0}{1}'.", JobStatus.Interrupted)]
    [InlineData("next", "clip.mp4 runs next.", JobStatus.Interrupted)]
    [InlineData("drop", "Dropped clip.mp4.", JobStatus.Cancelled)]
    public async Task AVideoACrashLeftConvertingIsStoppedFirstThenActedOn(string verb, string line, JobStatus expected)
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Encoding);

        var result = await RunAsync("queue", verb, job.Id.ToString("N"));

        result.Should().Be((0, Lines(string.Format(System.Globalization.CultureInfo.InvariantCulture, line, job.Id.ToString("N"), ConfigArgument)), ""));
        var stored = await StoredAsync(job);
        stored.Status.Should().Be(expected);
        stored.ResumeStatus.Should().Be(JobStatus.Encoding);
    }

    [Theory]
    [InlineData("hold", "be held", "ready", "is ready to finalise")]
    [InlineData("next", "run next", "ready", "is ready to finalise")]
    [InlineData("hold", "be held", "failed", "has already failed")]
    [InlineData("next", "run next", "dropped", "was already dropped")]
    public async Task OnlyWaitingAndStoppedVideosAreHeldOrMovedNext(string verb, string action, string state, string phrase)
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, state switch { "ready" => [JobStatus.Validating, JobStatus.ReadyToFinalize], "failed" => [JobStatus.Failed], _ => [JobStatus.Cancelled] });

        (await RunAsync("queue", verb, job.Id.ToString("N"))).Should().Be((1, "", Lines($"Only waiting and stopped videos can {action}; clip.mp4 {phrase}.")));
        (await StoredAsync(job)).Held.Should().BeFalse();
    }

    /// <param name="marked">Whether a run has marked it stopped yet; until then a crash leaves it Finalizing, with no finalise holding the lock.</param>
    [Theory]
    [InlineData("hold", true)]
    [InlineData("next", true)]
    [InlineData("drop", true)]
    [InlineData("hold", false)]
    public async Task AStoppedFinalisationBelongsToFinalize(string verb, bool marked)
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);
        if (marked) await _repository.MarkActiveJobsInterruptedAsync(DatabasePath, includeFinalizing: true);

        var result = await RunAsync("queue", verb, job.Id.ToString("N"));

        result.Should().Be((1, "", Lines($"clip.mp4 was stopped while being finalised; 'video-optimiser finalize {job.Id:N}{ConfigArgument}' checks and resumes it.")));
        (await StoredAsync(job)).Status.Should().Be(marked ? JobStatus.Interrupted : JobStatus.Finalizing);
    }

    /// <summary>R9: the recovery marks only what no live process owns; a finalise holding its lock keeps its job Finalizing.</summary>
    [Fact]
    public async Task RecoveryLeavesTheJobOfAFinaliseGoingAlone()
    {
        var finalising = await QueuedAsync("finalising.mp4");
        await MoveAsync(finalising, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Encoding);
        using var finalise = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(DatabasePath), "finalize", out _);

        (await RunAsync("queue", "hold", job.Id.ToString("N"))).ExitCode.Should().Be(0);

        (await StoredAsync(finalising)).Status.Should().Be(JobStatus.Finalizing);
        (await StoredAsync(job)).Status.Should().Be(JobStatus.Interrupted);
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("drop")]
    public async Task AVideoBeingFinalisedIsLeftToTheFinalise(string verb)
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);
        using var finalise = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(DatabasePath), "finalize", out _);

        (await RunAsync("queue", verb, job.Id.ToString("N"))).Should().Be((1, "", Lines("clip.mp4 is being finalised now.")));
        (await StoredAsync(job)).Status.Should().Be(JobStatus.Finalizing);
    }

    [Fact]
    public async Task DropEndsAWaitingVideo()
    {
        var job = await HeldAsync(await QueuedAsync("clip.mp4"));

        (await RunAsync("queue", "drop", job.Id.ToString("N"))).Should().Be((0, Lines("Dropped clip.mp4."), ""));
        var stored = await StoredAsync(job);
        stored.Status.Should().Be(JobStatus.Cancelled);
        stored.Held.Should().BeFalse();
    }

    /// <summary>What cleanup will delete of the dropped video, read from cleanup's own plan.</summary>
    [Theory]
    [InlineData("ready", "Its AV1 stays until 'video-optimiser cleanup{0}' deletes it.")]
    [InlineData("stopped encode", "Its unfinished AV1 stays until 'video-optimiser cleanup{0}' deletes it.")]
    [InlineData("immich waiting", "Its downloaded copy stays until 'video-optimiser cleanup{0}' deletes it.")]
    [InlineData("immich ready", "Its AV1 and downloaded copy stay until 'video-optimiser cleanup{0}' deletes them.")]
    [InlineData("immich stopped encode", "Its unfinished AV1 and downloaded copy stay until 'video-optimiser cleanup{0}' deletes them.")]
    [InlineData("ready, AV1 gone", null)]
    public async Task DropSaysWhichFilesStayUntilCleanup(string state, string? line)
    {
        var immich = state.StartsWith("immich", StringComparison.Ordinal);
        var job = immich ? await ImmichQueuedAsync() : await QueuedAsync("clip.mp4");
        if (state.EndsWith("ready", StringComparison.Ordinal) || state.StartsWith("ready", StringComparison.Ordinal)) await ReadyAsync(job);
        // Only its validation record is left, which is no AV1.
        if (state.EndsWith("AV1 gone", StringComparison.Ordinal)) File.Delete(job.OutputPath!);
        if (state.EndsWith("stopped encode", StringComparison.Ordinal))
        {
            await StopAsync(job, JobStatus.Encoding);
            await WriteAv1Async(job);
        }
        _immich.Asset = immich ? Asset(job) : null;

        var result = await RunAsync("queue", "drop", job.Id.ToString("N"));

        string[] expected = line is null ? [$"Dropped {Path.GetFileName(job.SourcePath)}."] : [$"Dropped {Path.GetFileName(job.SourcePath)}.", string.Format(System.Globalization.CultureInfo.InvariantCulture, line, ConfigArgument)];
        result.Should().Be((0, Lines(expected), ""));
    }

    /// <summary>W3, VOPT-90: dropping does not stop a CRF search or encode left running on the worker.</summary>
    [Theory]
    [InlineData(RemoteExecutionIdentity.ManagedHetzner, JobStatus.Encoding, "Its encode may still be running on the worker; deleting the worker stops it.")]
    [InlineData(RemoteExecutionIdentity.ManagedHetzner, JobStatus.CrfSearching, "Its CRF search may still be running on the worker; deleting the worker stops it.")]
    [InlineData("video-worker", JobStatus.Encoding, "Its encode may still be running on the worker until it finishes.")]
    public async Task DroppingAStoppedRemoteStageSaysItMayStillBeRunning(string host, JobStatus stage, string line)
    {
        var job = await QueuedAsync("clip.mp4", queued => { queued.ExecutionMode = ProcessingModes.RemoteSsh; queued.RemoteHost = host; queued.RemoteWorkspace = $"/w/{queued.Id:N}"; });
        await StopAsync(job, stage);

        (await RunAsync("queue", "drop", job.Id.ToString("N"))).Should().Be((0, Lines("Dropped clip.mp4.", line), ""));
    }

    [Fact]
    public async Task DroppingAnImmichVideoWhoseAv1IsUploadedSaysItStaysInImmich()
    {
        var job = await ImmichQueuedAsync(queued => queued.ImmichNewAssetId = "new-asset");
        await ReadyAsync(job);
        _immich.Failure = new InvalidOperationException("Immich is not asked: the AV1 is already there.");

        var result = await RunAsync("queue", "drop", job.Id.ToString("N"));

        result.Should().Be((0, Lines(
            "Dropped IMG_0001.MOV.",
            $"Its AV1 and downloaded copy stay until 'video-optimiser cleanup{ConfigArgument}' deletes them.",
            "The AV1 already uploaded to Immich as asset new-asset stays there; delete it in Immich if you do not want it."), ""));
    }

    [Fact]
    public async Task DropAllDropsTheQueueAndLeavesFinishedAndFinalisingVideosWithTheirCommands()
    {
        var waiting = await QueuedAsync("a.mp4");
        var held = await HeldAsync(await QueuedAsync("b.mp4"));
        var checking = await QueuedAsync("c.mp4");
        await StopAsync(checking, JobStatus.Validating);
        var ready = await ReadyAsync(await QueuedAsync("d.mp4"));
        var finalising = await QueuedAsync("e.mp4");
        await MoveAsync(finalising, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);
        await _repository.MarkActiveJobsInterruptedAsync(DatabasePath, includeFinalizing: true);
        var crashed = await QueuedAsync("f.mp4");
        await MoveAsync(crashed, JobStatus.Encoding);

        var result = await RunAsync("queue", "drop", "--all");

        result.Should().Be((0, Lines(
            "Dropped a.mp4.",
            "Dropped b.mp4.",
            "Dropped f.mp4.",
            $"Left 1 video stopped while being finalised; 'video-optimiser finalize {finalising.Id:N}{ConfigArgument}' checks and resumes it.",
            $"Left 1 video stopped while its AV1 was checked; to drop it: video-optimiser queue drop {checking.Id:N}{ConfigArgument}"), ""));
        (await StoredAsync(waiting)).Status.Should().Be(JobStatus.Cancelled);
        (await StoredAsync(held)).Status.Should().Be(JobStatus.Cancelled);
        (await StoredAsync(checking)).Status.Should().Be(JobStatus.Interrupted);
        (await StoredAsync(ready)).Status.Should().Be(JobStatus.ReadyToFinalize);
        (await StoredAsync(crashed)).Status.Should().Be(JobStatus.Cancelled);
    }

    /// <summary>With nothing converting there is no recovery to mark it, so a finalisation a crash left reads Finalizing.</summary>
    [Fact]
    public async Task DropAllLeavesAFinalisationACrashLeftWithItsCommand()
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);

        (await RunAsync("queue", "drop", "--all")).Should().Be((0, Lines($"Left 1 video stopped while being finalised; 'video-optimiser finalize {job.Id:N}{ConfigArgument}' checks and resumes it."), ""));
    }

    [Fact]
    public async Task DropAllWithNothingQueuedSaysTheQueueIsEmpty()
    {
        await ReadyAsync(await QueuedAsync("ready.mp4"));

        (await RunAsync("queue", "drop", "--all")).Should().Be((0, Lines("Nothing to drop: the queue is empty."), ""));
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("release")]
    [InlineData("next")]
    [InlineData("drop")]
    public async Task AVerbNamesWhatIsWrongWithTheJobId(string verb)
    {
        await WriteConfigAsync();
        var unknown = Guid.NewGuid();

        (await RunAsync("queue", verb, "3f2a")).Should().Be((2, "", Lines($"'3f2a' is not a job ID. 'video-optimiser queue list{ConfigArgument}' shows them.")));
        (await RunAsync("queue", verb, unknown.ToString("N"))).Should().Be((2, "", Lines($"No job has ID {unknown:N}.")));
    }

    [Theory]
    [InlineData(JobStatus.Failed, true, "has already failed")]
    [InlineData(JobStatus.Cancelled, false, "was already dropped")]
    [InlineData(JobStatus.Completed, false, "was already finalised")]
    [InlineData(JobStatus.Skipped, false, "was not worth converting")]
    public async Task DroppingAnEndedVideoSaysSoAndPointsAtCleanupOnlyWhenItHasFiles(JobStatus status, bool leftFiles, string phrase)
    {
        var job = await QueuedAsync("clip.mp4");
        if (status == JobStatus.Completed) await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);
        await MoveAsync(job, status);
        if (leftFiles) await WriteAv1Async(job);

        var result = await RunAsync("queue", "drop", job.Id.ToString("N"));

        string[] expected = leftFiles
            ? [$"clip.mp4 {phrase}, so there is nothing to drop.", $"'video-optimiser cleanup{ConfigArgument}' deletes its temporary files."]
            : [$"clip.mp4 {phrase}, so there is nothing to drop."];
        result.Should().Be((1, "", Lines(expected)));
    }

    [Fact]
    public async Task DroppingAReadyVideoWaitsForNoFinaliseAndRefusesDuringOne()
    {
        var job = await ReadyAsync(await QueuedAsync("clip.mp4"));
        using var finalise = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(DatabasePath), "finalize", out _);

        (await RunAsync("queue", "drop", job.Id.ToString("N"))).Should().Be((1, "", Lines("A finalise is running on this database. Wait for it to finish, then drop clip.mp4.")));
        (await StoredAsync(job)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    /// <summary>R5, W1: cleanup deletes a dropped job's AV1, which is the only copy once the original is gone.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFinishedAv1WhoseOriginalIsMissingIsNotDropped(bool stoppedWhileChecking)
    {
        var job = await QueuedAsync("clip.mp4");
        if (stoppedWhileChecking)
        {
            await StopAsync(job, JobStatus.Validating);
            await WriteAv1Async(job);
        }
        else
        {
            await ReadyAsync(job);
        }
        File.Delete(job.SourcePath);

        var result = await RunAsync("queue", "drop", job.Id.ToString("N"));

        result.Should().Be((1, "", Lines($"The original of clip.mp4 is missing, so its AV1 at {job.OutputPath} may be the only copy. Put the original back, or move the AV1 somewhere safe, then drop the video.")));
        (await StoredAsync(job)).Status.Should().NotBe(JobStatus.Cancelled);
    }

    [Fact]
    public async Task AFolderVideoWhoseAv1WasMovedAwayIsDroppedWithoutItsOriginal()
    {
        var job = await ReadyAsync(await QueuedAsync("clip.mp4"));
        File.Delete(job.SourcePath);
        File.Delete(job.OutputPath!);

        (await RunAsync("queue", "drop", job.Id.ToString("N"))).Should().Be((0, Lines("Dropped clip.mp4."), ""));
    }

    [Fact]
    public async Task AnImmichVideoWithNoLocalCopyLeftIsDroppedWithoutAskingImmich()
    {
        var job = await ReadyAsync(await ImmichQueuedAsync());
        File.Delete(job.SourcePath);
        File.Delete(job.OutputPath!);
        _immich.Failure = new InvalidOperationException("Immich is not asked: nothing on this PC could be the only copy.");

        (await RunAsync("queue", "drop", job.Id.ToString("N"))).Should().Be((0, Lines("Dropped IMG_0001.MOV."), ""));
    }

    /// <summary>R5, W2: an Immich original deleted, in the trash (which empties) or replaced leaves the local files as the only copies.</summary>
    [Theory]
    [InlineData("deleted")]
    [InlineData("trashed")]
    [InlineData("replaced")]
    public async Task AnImmichVideoWhoseOriginalIsGoneFromImmichIsNotDropped(string gone)
    {
        var job = await ReadyAsync(await ImmichQueuedAsync());
        _immich.Asset = gone == "deleted" ? null : Asset(job) with { IsTrashed = gone == "trashed", Checksum = gone == "replaced" ? "other" : job.ImmichChecksum! };

        var result = await RunAsync("queue", "drop", job.Id.ToString("N"));

        result.Should().Be((1, "", Lines($"The original of IMG_0001.MOV is gone from Immich, so its AV1 and downloaded copy may be the only copies: {job.OutputPath}, {job.SourcePath}. Move them somewhere safe, then drop the video.")));
        (await StoredAsync(job)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    [Theory]
    [InlineData(false, "immich.baseUrl isn't set")]
    [InlineData(true, "Immich is unreachable at http://immich.test: refused")]
    public async Task AnImmichVideoIsNotDroppedWhileImmichCannotSayItsOriginalIsThere(bool configured, string message)
    {
        var job = await ReadyAsync(await ImmichQueuedAsync(immichConfigured: configured));
        _immich.Failure = new ImmichApiException("ImmichUnavailable", "Immich is unreachable at http://immich.test: refused.");

        var result = await RunAsync("queue", "drop", job.Id.ToString("N"));

        result.Should().Be((1, "", Lines($"Immich can't be used ({message}), so video-optimiser can't check that the original of IMG_0001.MOV is still there. Try again once it answers.")));
        (await StoredAsync(job)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    /// <summary>R1: a verb that loses a race to another command leaves that command's change standing and says to try again.</summary>
    [Fact]
    public async Task AVerbThatLosesARaceSaysToRunItAgain()
    {
        var job = await QueuedAsync("clip.mp4");
        var racing = new RacingRepository(_repository);

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(racing), "queue", "hold", job.Id.ToString("N"));

        result.Should().Be((1, "", Lines($"Job {job.Id:N} changed while this command was using it. Run the command again.")));
        (await StoredAsync(job)).Held.Should().BeFalse();
    }

    [Fact]
    public async Task QueueCancelSaysItIsNowQueueDrop()
    {
        var (code, _, error) = await ConsoleCapture.RunCliAsync("queue", "cancel", "--all");

        code.Should().Be(2);
        error.Should().StartWith(Lines("'queue cancel' is now 'queue drop'."));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) => RunAsync(_ => { }, args);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(Action<IServiceCollection> configure, params string[] args)
    {
        if (_config.Length == 0) await WriteConfigAsync();
        return await ConsoleCapture.RunCliAsync(services =>
        {
            services.AddSingleton<IImmichClientFactory>(_immich);
            configure(services);
        }, [.. args, "--config", _config]);
    }

    private async Task WriteConfigAsync(bool immichConfigured = false)
    {
        _config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(_config, string.Join('\n',
            "database:",
            $"  path: '{DatabasePath}'",
            "immich:",
            immichConfigured ? "  baseUrl: 'http://immich.test'" : "",
            $"  stagingDirectory: '{StagingDirectory}'",
            ""));
    }

    private async Task<JobRecord> QueuedAsync(string name, Action<JobRecord>? configure = null)
    {
        var source = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(source, "original");
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = "original", Status = JobStatus.Queued };
        configure?.Invoke(job);
        await _repository.CreateAsync(DatabasePath, job);
        await Task.Delay(5);
        return job;
    }

    private async Task<JobRecord> ImmichQueuedAsync(Action<JobRecord>? configure = null, bool immichConfigured = true)
    {
        await WriteConfigAsync(immichConfigured);
        var assetId = Guid.NewGuid().ToString("D");
        var source = Path.Combine(StagingDirectory, assetId, "IMG_0001.MOV");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "original");
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = "original", Status = JobStatus.Queued, SourceKind = JobSourceKind.Immich, ImmichAssetId = assetId, ImmichChecksum = "checksum" };
        configure?.Invoke(job);
        await _repository.CreateAsync(DatabasePath, job);
        return job;
    }

    private async Task MoveAsync(JobRecord job, params JobStatus[] steps)
    {
        foreach (var step in steps)
        {
            job.Status = step;
            await _repository.UpdateAsync(DatabasePath, job);
        }
    }

    private async Task StopAsync(JobRecord job, JobStatus stage)
    {
        await MoveAsync(job, stage);
        job.ResumeStatus = stage;
        await MoveAsync(job, JobStatus.Interrupted);
    }

    private async Task<JobRecord> ReadyAsync(JobRecord job)
    {
        await WriteAv1Async(job);
        await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize);
        return job;
    }

    /// <summary>The AV1 of attempt 1 and its manifest, where the processor writes them, recorded on the job.</summary>
    private async Task WriteAv1Async(JobRecord job)
    {
        var folder = Path.Combine(Path.GetDirectoryName(job.SourcePath)!, TemporaryOutputClassifier.FolderName);
        Directory.CreateDirectory(folder);
        job.Attempt = 1;
        job.OutputPath = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(job.SourcePath)}.{job.Id:N}.1.encoding.mp4");
        await File.WriteAllTextAsync(job.OutputPath, "av1");
        await File.WriteAllTextAsync(job.OutputPath + ".manifest.json", "{}");
        await _repository.UpdateAsync(DatabasePath, job);
    }

    private async Task<JobRecord> HeldAsync(JobRecord job)
    {
        (await _repository.SetHeldAsync(DatabasePath, job, held: true)).Should().BeTrue();
        return job;
    }

    private async Task<JobRecord> StoredAsync(JobRecord job) => (await _repository.GetAsync(DatabasePath, job.Id))!;

    private static ImmichAsset Asset(JobRecord job) => new(job.ImmichAssetId!, job.ImmichChecksum!, "IMG_0001.MOV", ImmichValues.Video, ImmichValues.Timeline, false, false, null, null,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, new ImmichExif(null, null, null, null, null, null, null, null), []);

    /// <summary>Changes the job from "another terminal" just before the verb writes it, so the verb's copy is stale.</summary>
    private sealed class RacingRepository(IJobRepository inner) : DelegatingJobRepository(inner)
    {
        public override async Task<bool> SetHeldAsync(string databasePath, JobRecord job, bool held, CancellationToken cancellationToken = default)
        {
            await ChangeBehindTheCommandAsync(databasePath, job.Id, cancellationToken);
            return await Inner.SetHeldAsync(databasePath, job, held, cancellationToken);
        }
    }

    /// <summary>Answers only the asset lookup drop makes: <see cref="Asset"/>, or 404 when it is null, or <see cref="Failure"/>.</summary>
    private sealed class FakeImmich : IImmichClientFactory, IImmichClient
    {
        public ImmichAsset? Asset { get; set; }
        public Exception? Failure { get; set; }

        public IImmichClient Create(ImmichSettings settings) => this;

        public Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default) =>
            Failure is not null ? Task.FromException<ImmichAsset>(Failure)
            : Asset is not null ? Task.FromResult(Asset)
            : Task.FromException<ImmichAsset>(new ImmichApiException("ImmichRequestFailed", "Immich returned HTTP 400 for GET /api/assets.", 400));

        public Task<ImmichServerVersion> GetServerVersionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichUser> GetCurrentUserAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichTrashSettings> GetTrashSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ImmichAsset> SearchVideosAsync(DateTimeOffset takenBefore, bool includeFavorites, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadOriginalAsync(string assetId, string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichUploadResult> UploadAsync(ImmichUpload upload, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateMetadataAsync(string assetId, ImmichMetadataUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyRelationsAsync(string sourceAssetId, string targetAssetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TagAssetAsync(string assetId, IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TrashAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ImmichUploadCheck>> CheckUploadsAsync(IReadOnlyList<string> checksums, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
