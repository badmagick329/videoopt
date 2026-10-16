using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Cleanup;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using static VideoOptimiser.UnitTests.RunCli;

namespace VideoOptimiser.UnitTests.Features.Queue;

/// <summary>
/// <c>queue hold</c>, <c>release</c>, <c>next</c>, <c>drop</c> (VOPT-54 step 5) and <c>reject</c> (VOPT-112) through the real CLI: every approved line,
/// whole, with its exit code, and what each leaves in the database. A run is "going" when its lock is held, which these tests
/// do in-process with <see cref="DatabaseLock"/>.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class QueueVerbTests : QueueTestBase
{
    private readonly FakeImmich _immich = new();

    protected override void RegisterFakes(IServiceCollection services) => services.AddSingleton<IImmichClientFactory>(_immich);

    /// <summary>A stopped video keeps its resume point through a hold too; <see cref="AVideoACrashLeftConvertingIsStoppedFirstThenActedOn"/> pins that.</summary>
    [Fact]
    public async Task HoldParksAWaitingVideo()
    {
        var job = await QueuedAsync("clip.mp4");

        var result = await RunAsync("queue", "hold", job.Id.ToString("N"));

        result.Should().Be((0, Transcript($"Held clip.mp4. Runs pass over it until 'video-optimiser queue release {job.Id:N}{ConfigArgument}'."), ""));
        var stored = await StoredAsync(job);
        stored.Held.Should().BeTrue();
        stored.ResumeStatus.Should().BeNull();
    }

    [Fact]
    public async Task HoldOnAHeldVideoSaysSo()
    {
        var job = await HeldAsync(await QueuedAsync("clip.mp4"));

        (await RunAsync("queue", "hold", job.Id.ToString("N"))).Should().Be((0, Transcript("clip.mp4 is already held."), ""));
    }

    [Theory]
    [InlineData(true, "Released clip.mp4; runs take it again.")]
    [InlineData(false, "clip.mp4 isn't held.")]
    public async Task ReleaseTakesAHeldVideoBack(bool held, string line)
    {
        var job = await QueuedAsync("clip.mp4");
        if (held) await HeldAsync(job);

        (await RunAsync("queue", "release", job.Id.ToString("N"))).Should().Be((0, Transcript(line), ""));
        (await StoredAsync(job)).Held.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, null, "clip.mp4 runs next; it's no longer held.")]
    [InlineData(false, "video-optimiser queue process", "clip.mp4 is now first in the queue.")]
    [InlineData(false, "video-optimiser run", "clip.mp4 runs next.")]
    [InlineData(false, "queue drop", "clip.mp4 runs next.")]
    public async Task NextPutsAVideoFirstAndSaysWhetherTheRunGoingNowTakesIt(bool held, string? runGoing, string line)
    {
        var earlier = await QueuedAsync("earlier.mp4");
        var job = await QueuedAsync("clip.mp4");
        if (held) await HeldAsync(job);
        using var run = runGoing is null ? null : DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), runGoing, out _);

        var result = await RunAsync("queue", "next", job.Id.ToString("N"));

        result.Should().Be((0, Transcript(line), ""));
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

        (await RunAsync("queue", "next", job.Id.ToString("N"))).Should().Be((0, Transcript("clip.mp4 is converting now."), ""));
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

        (await RunAsync("queue", verb, job.Id.ToString("N"))).Should().Be((1, "", Transcript($"clip.mp4 is converting now (encoding). {advice}, press Ctrl+C twice in the run's window first.")));
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

        result.Should().Be((0, Transcript(string.Format(System.Globalization.CultureInfo.InvariantCulture, line, job.Id.ToString("N"), ConfigArgument)), ""));
        var stored = await StoredAsync(job);
        stored.Status.Should().Be(expected);
        stored.ResumeStatus.Should().Be(JobStatus.Encoding);
    }

    [Theory]
    [InlineData("hold", "be held")]
    [InlineData("next", "run next")]
    public async Task OnlyWaitingAndStoppedVideosAreHeldOrMovedNext(string verb, string action)
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize);

        (await RunAsync("queue", verb, job.Id.ToString("N"))).Should().Be((1, "", Transcript($"Only waiting and stopped videos can {action}; clip.mp4 is ready to finalise.")));
        (await StoredAsync(job)).Held.Should().BeFalse();
    }

    /// <param name="marked">Whether a run has marked it stopped yet; until then a crash leaves it Finalizing, with no finalise holding the lock.</param>
    [Theory]
    [InlineData("hold", true)]
    [InlineData("next", true)]
    [InlineData("drop", true)]
    [InlineData("reject", true)]
    [InlineData("hold", false)]
    [InlineData("reject", false)]
    public async Task AStoppedFinalisationBelongsToFinalize(string verb, bool marked)
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);
        if (marked) await Repository.MarkActiveJobsInterruptedAsync(DatabasePath, includeFinalizing: true);

        var result = await RunAsync("queue", verb, job.Id.ToString("N"));

        result.Should().Be((1, "", Transcript($"clip.mp4 was stopped while being finalised; 'video-optimiser finalize {job.Id:N}{ConfigArgument}' checks and resumes it.")));
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
    [InlineData("reject")]
    public async Task AVideoBeingFinalisedIsLeftToTheFinalise(string verb)
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);
        using var finalise = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(DatabasePath), "finalize", out _);

        (await RunAsync("queue", verb, job.Id.ToString("N"))).Should().Be((1, "", Transcript("clip.mp4 is being finalised now.")));
        (await StoredAsync(job)).Status.Should().Be(JobStatus.Finalizing);
    }

    [Fact]
    public async Task DropEndsAWaitingVideo()
    {
        var job = await HeldAsync(await QueuedAsync("clip.mp4"));

        (await RunAsync("queue", "drop", job.Id.ToString("N"))).Should().Be((0, Transcript("Dropped clip.mp4."), ""));
        var stored = await StoredAsync(job);
        stored.Status.Should().Be(JobStatus.Cancelled);
        stored.Held.Should().BeFalse();
    }

    /// <summary>What cleanup will delete of the dropped video, read from cleanup's own plan.</summary>
    [Theory]
    [InlineData("ready", "Its AV1 stays until 'video-optimiser cleanup{0}' deletes it.")]
    [InlineData("immich waiting", "Its downloaded copy stays until 'video-optimiser cleanup{0}' deletes it.")]
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
        result.Should().Be((0, Transcript(expected), ""));
    }

    /// <summary>
    /// W3, VOPT-90: dropping does not stop a CRF search or encode left running on the worker. A managed worker's deletion ends
    /// it; <see cref="DropAllDropsTheQueueAndLeavesFinishedAndFinalisingVideosWithTheirCommands"/> has the manual worker's
    /// CRF search.
    /// </summary>
    [Fact]
    public async Task DroppingAStoppedRemoteStageSaysItMayStillBeRunning()
    {
        var job = await QueuedAsync("clip.mp4", RemoteOn(RemoteExecutionIdentity.ManagedHetzner));
        await StopAsync(job, JobStatus.Encoding);

        (await RunAsync("queue", "drop", job.Id.ToString("N"))).Should().Be((0, Transcript("Dropped clip.mp4.", "Its encode may still be running on the worker; the next run on that worker stops it."), ""));
    }

    [Fact]
    public async Task DroppingAnImmichVideoWhoseAv1IsUploadedSaysItStaysInImmich()
    {
        var job = await ImmichQueuedAsync(queued => queued.ImmichNewAssetId = "new-asset");
        await ReadyAsync(job);
        _immich.Failure = new InvalidOperationException("Immich is not asked: the AV1 is already there.");

        var result = await RunAsync("queue", "drop", job.Id.ToString("N"));

        result.Should().Be((0, Transcript(
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
        await Repository.MarkActiveJobsInterruptedAsync(DatabasePath, includeFinalizing: true);
        var crashed = await QueuedAsync("f.mp4");
        await MoveAsync(crashed, JobStatus.Encoding);
        var remote = await QueuedAsync("g.mp4", RemoteOn("video-worker"));
        await StopAsync(remote, JobStatus.CrfSearching);

        var result = await RunAsync("queue", "drop", "--all");

        result.Should().Be((0, Transcript(
            "Dropped a.mp4.",
            "Dropped b.mp4.",
            "Dropped f.mp4.",
            "Dropped g.mp4.",
            "Its CRF search may still be running on the worker; the next run on that worker stops it.",
            $"Left 1 video stopped while being finalised; 'video-optimiser finalize {finalising.Id:N}{ConfigArgument}' checks and resumes it.",
            $"Left 1 video stopped while its AV1 was checked; to drop it: video-optimiser queue drop {checking.Id:N}{ConfigArgument}"), ""));
        (await StoredAsync(waiting)).Status.Should().Be(JobStatus.Cancelled);
        (await StoredAsync(held)).Status.Should().Be(JobStatus.Cancelled);
        (await StoredAsync(checking)).Status.Should().Be(JobStatus.Interrupted);
        // A finalisation that moved files is left exactly as it was, so `finalize` can check what moved (VOPT-70).
        var stoppedFinalising = await StoredAsync(finalising);
        (stoppedFinalising.Status, stoppedFinalising.ResumeStatus).Should().Be((JobStatus.Interrupted, JobStatus.Finalizing));
        (await StoredAsync(ready)).Status.Should().Be(JobStatus.ReadyToFinalize);
        (await StoredAsync(crashed)).Status.Should().Be(JobStatus.Cancelled);
        (await StoredAsync(remote)).Status.Should().Be(JobStatus.Cancelled);
    }

    /// <summary>With nothing converting there is no recovery to mark it, so a finalisation a crash left reads Finalizing.</summary>
    [Fact]
    public async Task DropAllLeavesAFinalisationACrashLeftWithItsCommand()
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);

        (await RunAsync("queue", "drop", "--all")).Should().Be((0, Transcript($"Left 1 video stopped while being finalised; 'video-optimiser finalize {job.Id:N}{ConfigArgument}' checks and resumes it."), ""));
    }

    [Fact]
    public async Task DropAllWithNothingQueuedSaysTheQueueIsEmpty()
    {
        await ReadyAsync(await QueuedAsync("ready.mp4"));

        (await RunAsync("queue", "drop", "--all")).Should().Be((0, Transcript("Nothing to drop: the queue is empty."), ""));
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("release")]
    [InlineData("next")]
    [InlineData("drop")]
    [InlineData("reject")]
    public async Task AVerbNamesWhatIsWrongWithTheJobId(string verb)
    {
        await WriteConfigAsync();
        var unknown = Guid.NewGuid();

        (await RunAsync("queue", verb, "3f2a")).Should().Be((2, "", Transcript($"'3f2a' is not a job ID. 'video-optimiser queue list{ConfigArgument}' shows them.")));
        (await RunAsync("queue", verb, unknown.ToString("N"))).Should().Be((2, "", Transcript($"No job has ID {unknown:N}.")));
    }

    /// <summary>Cleanup deletes a failed video's unfinished AV1, or an Immich video's staged copy though no AV1 is left, so the refusal points at it for either.</summary>
    [Theory]
    [InlineData(JobStatus.Failed, "AV1", "has already failed")]
    [InlineData(JobStatus.Failed, "staged copy", "has already failed")]
    [InlineData(JobStatus.Cancelled, "none", "was already dropped")]
    [InlineData(JobStatus.Completed, "none", "was already finalised")]
    [InlineData(JobStatus.Skipped, "none", "was not worth converting")]
    public async Task DroppingAnEndedVideoSaysSoAndPointsAtCleanupOnlyWhenItHasFiles(JobStatus status, string leftFiles, string phrase)
    {
        var job = leftFiles == "staged copy" ? await ImmichQueuedAsync() : await QueuedAsync("clip.mp4");
        if (status == JobStatus.Completed) await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);
        await MoveAsync(job, status);
        if (leftFiles == "AV1") await WriteAv1Async(job);
        // Cleanup keeps a downloaded copy while Immich cannot show the original is still there.
        _immich.Asset = Asset(job);

        var result = await RunAsync("queue", "drop", job.Id.ToString("N"));

        var name = Path.GetFileName(job.SourcePath);
        string[] expected = leftFiles != "none"
            ? [$"{name} {phrase}, so there is nothing to drop.", $"'video-optimiser cleanup{ConfigArgument}' deletes its temporary files."]
            : [$"{name} {phrase}, so there is nothing to drop."];
        result.Should().Be((1, "", Transcript(expected)));
    }

    /// <summary>A line that reports a completed write reaches the console before any later read that could throw, so a user whose drop succeeded never sees only an error and believes it failed.</summary>
    [Fact]
    public async Task ADropThatSucceededIsReportedEvenWhenCleanupsPlanCannotBeRead()
    {
        var job = await QueuedAsync("clip-a.mp4");

        var (code, stdout, stderr) = await RunAsync(services => services.AddSingleton<ITemporaryOutputCleanupService>(new UnreadablePlan()), "queue", "drop", job.Id.ToString("N"));

        code.Should().NotBe(0);
        stdout.Should().StartWith(Transcript("Dropped clip-a.mp4."));
        stderr.Should().Contain("plan unreadable");
        (await StoredAsync(job)).Status.Should().Be(JobStatus.Cancelled);
    }

    [Fact]
    public async Task DroppingAReadyVideoWaitsForNoFinaliseAndRefusesDuringOne()
    {
        var job = await ReadyAsync(await QueuedAsync("clip.mp4"));
        using var finalise = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(DatabasePath), "finalize", out _);

        (await RunAsync("queue", "drop", job.Id.ToString("N"))).Should().Be((1, "", Transcript("A finalise is running on this database. Wait for it to finish, then drop clip.mp4.")));
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

        result.Should().Be((1, "", Transcript($"The original of clip.mp4 is missing, so its AV1 at {job.OutputPath} may be the only copy. Put the original back, or move the AV1 somewhere safe, then drop the video.")));
        (await StoredAsync(job)).Status.Should().NotBe(JobStatus.Cancelled);
    }

    [Fact]
    public async Task AFolderVideoWhoseAv1WasMovedAwayIsDroppedWithoutItsOriginal()
    {
        var job = await ReadyAsync(await QueuedAsync("clip.mp4"));
        File.Delete(job.SourcePath);
        File.Delete(job.OutputPath!);

        (await RunAsync("queue", "drop", job.Id.ToString("N"))).Should().Be((0, Transcript("Dropped clip.mp4."), ""));
    }

    [Fact]
    public async Task AnImmichVideoWithNoLocalCopyLeftIsDroppedWithoutAskingImmich()
    {
        var job = await ReadyAsync(await ImmichQueuedAsync());
        File.Delete(job.SourcePath);
        File.Delete(job.OutputPath!);
        _immich.Failure = new InvalidOperationException("Immich is not asked: nothing on this PC could be the only copy.");

        (await RunAsync("queue", "drop", job.Id.ToString("N"))).Should().Be((0, Transcript("Dropped IMG_0001.MOV."), ""));
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

        result.Should().Be((1, "", Transcript($"The original of IMG_0001.MOV is gone from Immich, so its AV1 and downloaded copy may be the only copies: {job.OutputPath}, {job.SourcePath}. Move them somewhere safe, then drop the video.")));
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

        result.Should().Be((1, "", Transcript($"Immich can't be used ({message}), so video-optimiser can't check that the original of IMG_0001.MOV is still there. Try again once it answers.")));
        (await StoredAsync(job)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    /// <summary>R1: a verb that loses a race to another command leaves that command's change standing and says to try again.</summary>
    [Fact]
    public async Task AVerbThatLosesARaceSaysToRunItAgain()
    {
        var job = await QueuedAsync("clip.mp4");
        var racing = new RacingRepository(Repository);

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(racing), "queue", "hold", job.Id.ToString("N"));

        result.Should().Be((1, "", Transcript($"Job {job.Id:N} changed while this command was using it. Run the command again.")));
        (await StoredAsync(job)).Held.Should().BeFalse();
    }

    [Fact]
    public async Task QueueCancelSaysItIsNowQueueDrop()
    {
        var (code, _, error) = await ConsoleCapture.RunCliAsync("queue", "cancel", "--all");

        code.Should().Be(2);
        error.Should().StartWith(Transcript("'queue cancel' is now 'queue drop'."));
    }

    // ---- reject (VOPT-112) ------------------------------------------------------------------

    private string RetryLine(JobRecord job) => $"To try it again: video-optimiser queue retry {job.Id:N}{ConfigArgument}";

    [Fact]
    public async Task RejectDeletesAFolderVideosAv1AndLeavesItsOriginalByteForByteUntouched()
    {
        var job = await ReadyAsync(await QueuedAsync("clip.mp4"));
        var original = await File.ReadAllBytesAsync(job.SourcePath);
        var written = File.GetLastWriteTimeUtc(job.SourcePath);
        var av1 = job.OutputPath!;

        var result = await RunAsync("queue", "reject", job.Id.ToString("N"));

        result.Should().Be((0, Transcript("Rejected clip.mp4. The original is untouched.", "Deleted its AV1.", RetryLine(job)), ""));
        File.Exists(av1).Should().BeFalse();
        File.Exists(av1 + ".manifest.json").Should().BeFalse();
        (await File.ReadAllBytesAsync(job.SourcePath)).Should().Equal(original);
        File.GetLastWriteTimeUtc(job.SourcePath).Should().Be(written);
        var stored = await StoredAsync(job);
        (stored.Status, stored.Held, stored.FailureCategory).Should().Be((JobStatus.Rejected, false, "Rejected"));
        stored.CompletedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task RejectDeletesAnImmichVideosAv1AndDownloadedCopy()
    {
        var job = await ReadyAsync(await ImmichQueuedAsync());
        _immich.Asset = Asset(job);

        var result = await RunAsync("queue", "reject", job.Id.ToString("N"));

        result.Should().Be((0, Transcript("Rejected IMG_0001.MOV. The original in Immich is untouched.", "Deleted its AV1 and downloaded copy.", RetryLine(job)), ""));
        File.Exists(job.OutputPath!).Should().BeFalse();
        File.Exists(job.SourcePath).Should().BeFalse();
        (await StoredAsync(job)).Status.Should().Be(JobStatus.Rejected);
    }

    /// <summary>A rejected video's AV1 is nothing a run or finalize would read again, so a second reject, or a drop, finds the job ended.</summary>
    [Fact]
    public async Task RejectingTwiceSaysThereIsNothingToReject()
    {
        var job = await ReadyAsync(await QueuedAsync("clip.mp4"));
        await RunAsync("queue", "reject", job.Id.ToString("N"));

        (await RunAsync("queue", "reject", job.Id.ToString("N"))).Should().Be((1, "", Transcript("clip.mp4 was already rejected, so there is nothing to reject.")));
        (await RunAsync("queue", "drop", job.Id.ToString("N"))).Should().Be((1, "", Transcript("clip.mp4 was already rejected, so there is nothing to drop.")));
    }

    /// <summary>A dropped ready video keeps its AV1 until cleanup, so rejecting it says that, as dropping again does.</summary>
    [Fact]
    public async Task RejectingADroppedVideoPointsAtCleanupForItsAv1()
    {
        var job = await ReadyAsync(await QueuedAsync("clip.mp4"));
        await RunAsync("queue", "drop", job.Id.ToString("N"));

        var result = await RunAsync("queue", "reject", job.Id.ToString("N"));

        result.Should().Be((1, "", Transcript("clip.mp4 was already dropped, so there is nothing to reject.", $"'video-optimiser cleanup{ConfigArgument}' deletes its temporary files.")));
        File.Exists(job.OutputPath!).Should().BeTrue();
    }

    [Theory]
    [InlineData("waiting", "is waiting")]
    [InlineData("converting", "is encoding")]
    [InlineData("stopped", "is stopped")]
    [InlineData("failed", "has already failed")]
    public async Task OnlyAReadyVideoIsRejected(string state, string phrase)
    {
        var job = await QueuedAsync("clip.mp4");
        switch (state)
        {
            case "converting": await MoveAsync(job, JobStatus.Encoding); break;
            case "stopped": await StopAsync(job, JobStatus.Encoding); break;
            case "failed": await MoveAsync(job, JobStatus.Failed); break;
        }

        var result = await RunAsync("queue", "reject", job.Id.ToString("N"));

        // A failed video is an ended one, which says it so; every other state is "not ready".
        var line = state == "failed" ? $"clip.mp4 {phrase}, so there is nothing to reject." : $"Only a video that is ready to finalise can be rejected; clip.mp4 {phrase}.";
        result.Should().Be((1, "", Transcript(line)));
        (await StoredAsync(job)).Status.Should().NotBe(JobStatus.Rejected);
    }

    /// <summary>Its upload may have made an AV1 asset in Immich already, which a reject would leave with no job to find it.</summary>
    [Theory]
    [InlineData("incomplete")]
    [InlineData("uploaded")]
    public async Task AnImmichVideoWhoseFinalisationStartedIsLeftToFinalize(string how)
    {
        var job = await ImmichQueuedAsync(queued => queued.ImmichNewAssetId = how == "uploaded" ? "new-asset" : null);
        await ReadyAsync(job);
        if (how == "incomplete")
        {
            FinalizationRules.ReturnToReady(job, "Immich stopped answering.");
            await Repository.UpdateAsync(DatabasePath, job);
        }
        _immich.Asset = Asset(job);

        var result = await RunAsync("queue", "reject", job.Id.ToString("N"));

        result.Should().Be((1, "", Transcript($"IMG_0001.MOV was stopped while being finalised; 'video-optimiser finalize {job.Id:N}{ConfigArgument}' checks and resumes it.")));
        File.Exists(job.OutputPath!).Should().BeTrue();
        File.Exists(job.SourcePath).Should().BeTrue();
        (await StoredAsync(job)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    [Fact]
    public async Task RejectingAReadyVideoRefusesDuringAFinalise()
    {
        var job = await ReadyAsync(await QueuedAsync("clip.mp4"));
        using var finalise = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(DatabasePath), "finalize", out _);

        (await RunAsync("queue", "reject", job.Id.ToString("N"))).Should().Be((1, "", Transcript("A finalise is running on this database. Wait for it to finish, then reject clip.mp4.")));
        File.Exists(job.OutputPath!).Should().BeTrue();
        (await StoredAsync(job)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    /// <summary>A folder video whose original is gone has its AV1 as the only copy, and a reject deletes the AV1.</summary>
    [Fact]
    public async Task AFolderVideoWhoseOriginalIsMissingIsNotRejected()
    {
        var job = await ReadyAsync(await QueuedAsync("clip.mp4"));
        File.Delete(job.SourcePath);

        var result = await RunAsync("queue", "reject", job.Id.ToString("N"));

        result.Should().Be((1, "", Transcript($"The original of clip.mp4 is missing, so its AV1 at {job.OutputPath} may be the only copy. Put the original back, or move the AV1 somewhere safe, then reject the video.")));
        File.Exists(job.OutputPath!).Should().BeTrue();
        (await StoredAsync(job)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnImmichVideoIsNotRejectedWhileItsOriginalMayBeGone(bool trashed)
    {
        var job = await ReadyAsync(await ImmichQueuedAsync());
        if (trashed) _immich.Asset = Asset(job) with { IsTrashed = true };
        else _immich.Failure = new ImmichApiException("ImmichUnavailable", "Immich is unreachable at http://immich.test: refused.");

        var result = await RunAsync("queue", "reject", job.Id.ToString("N"));

        var line = trashed
            ? $"The original of IMG_0001.MOV is gone from Immich, so its AV1 and downloaded copy may be the only copies: {job.OutputPath}, {job.SourcePath}. Move them somewhere safe, then reject the video."
            : "Immich can't be used (Immich is unreachable at http://immich.test: refused), so video-optimiser can't check that the original of IMG_0001.MOV is still there. Try again once it answers.";
        result.Should().Be((1, "", Transcript(line)));
        File.Exists(job.OutputPath!).Should().BeTrue();
        File.Exists(job.SourcePath).Should().BeTrue();
        (await StoredAsync(job)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    /// <summary>R1: a reject that loses a race changes nothing, files included, because the write comes before the deletion.</summary>
    [Fact]
    public async Task ARejectThatLosesARaceDeletesNothingAndSaysToRunItAgain()
    {
        var job = await ReadyAsync(await QueuedAsync("clip.mp4"));

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new RacingRepository(Repository)), "queue", "reject", job.Id.ToString("N"));

        result.Should().Be((1, "", Transcript($"Job {job.Id:N} changed while this command was using it. Run the command again.")));
        File.Exists(job.OutputPath!).Should().BeTrue();
        (await StoredAsync(job)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    /// <summary>
    /// An AV1 the user has open in a player cannot be deleted. That is not the reject's failure: the job stays rejected, the
    /// line says what stays, and cleanup deletes it later by the same plan.
    /// </summary>
    [Fact]
    public async Task AnAv1ThatCannotBeDeletedLeavesTheJobRejectedAndCleanupDeletesItLater()
    {
        var job = await ReadyAsync(await QueuedAsync("clip.mp4"));
        (int ExitCode, string Stdout, string Stderr) result;
        using (new FileStream(job.OutputPath!, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await RunAsync("queue", "reject", job.Id.ToString("N"));
        }

        result.Should().Be((0, Transcript(
            "Rejected clip.mp4. The original is untouched.",
            $"Couldn't delete its AV1; it may be open in another program. It stays until 'video-optimiser cleanup{ConfigArgument}' deletes it.",
            RetryLine(job)), ""));
        (await StoredAsync(job)).Status.Should().Be(JobStatus.Rejected);
        File.Exists(job.OutputPath!).Should().BeTrue();

        var cleanupService = new TemporaryOutputCleanupService(Repository, new OnlyCopyGuard(_immich));
        var plan = await cleanupService.FindAsync(DatabasePath, new ImmichSettings());
        plan.Abandoned.Should().ContainSingle(file => file.Path == job.OutputPath).Which.Reason.Should().Be("job rejected");
        cleanupService.Delete(plan).Failures.Should().BeEmpty();
        File.Exists(job.OutputPath!).Should().BeFalse();
    }

    /// <summary>Reject is how a video the user turned down is undone: retry queues it again, and an Immich video's deleted copy is downloaded again by the next run.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryQueuesARejectedVideoAgain(bool immich)
    {
        var job = immich ? await ReadyAsync(await ImmichQueuedAsync()) : await ReadyAsync(await QueuedAsync("clip.mp4"));
        if (immich) _immich.Asset = Asset(job);
        await RunAsync("queue", "reject", job.Id.ToString("N"));

        var result = await RunAsync("queue", "retry", job.Id.ToString("N"));

        var open = (await Repository.ListAsync(DatabasePath, terminal: false)).Should().ContainSingle().Which;
        (open.Status, open.SourcePath).Should().Be((JobStatus.Queued, job.SourcePath));
        string[] expected = immich
            ? [$"Queued IMG_0001.MOV again as job {open.Id:N}.", $"Its downloaded copy is gone; 'video-optimiser run{ConfigArgument}' downloads it again first.", $"Next: run 'video-optimiser run{ConfigArgument}'."]
            : [$"Queued clip.mp4 again as job {open.Id:N}.", $"Next: run 'video-optimiser run{ConfigArgument}'."];
        result.Should().Be((0, Transcript(expected), ""));
    }

    private static Action<JobRecord> RemoteOn(string host) => job =>
    {
        job.ExecutionMode = ProcessingModes.RemoteSsh;
        job.RemoteHost = host;
        job.RemoteWorkspace = $"/w/{job.Id:N}";
    };

    private async Task<JobRecord> ImmichQueuedAsync(Action<JobRecord>? configure = null, bool immichConfigured = true)
    {
        await WriteConfigAsync(immichConfigured: immichConfigured);
        var assetId = Guid.NewGuid().ToString("D");
        var source = Path.Combine(StagingDirectory, assetId, "IMG_0001.MOV");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "original");
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = "original", Status = JobStatus.Queued, SourceKind = JobSourceKind.Immich, ImmichAssetId = assetId, ImmichChecksum = "checksum" };
        configure?.Invoke(job);
        await Repository.CreateAsync(DatabasePath, job);
        return job;
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
        await Repository.UpdateAsync(DatabasePath, job);
    }

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

        public override async Task<bool> RejectAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default)
        {
            await ChangeBehindTheCommandAsync(databasePath, job.Id, cancellationToken);
            return await Inner.RejectAsync(databasePath, job, cancellationToken);
        }
    }

    private sealed class UnreadablePlan : ITemporaryOutputCleanupService
    {
        public Task<TemporaryOutputCleanupPlan> FindAsync(string databasePath, ImmichSettings immich, CancellationToken cancellationToken = default) =>
            throw new IOException("plan unreadable");

        public TemporaryOutputCleanupResult Delete(TemporaryOutputCleanupPlan plan) => throw new NotSupportedException();
    }

    /// <summary>
    /// Answers only the asset lookup drop makes: <see cref="Asset"/>, or 404 when it is null, or <see cref="Failure"/>. The
    /// stub's trash and upload throw, so a drop that touched Immich's library would fail its test.
    /// </summary>
    private sealed class FakeImmich : ImmichClientStub
    {
        public ImmichAsset? Asset { get; set; }
        public Exception? Failure { get; set; }

        public override Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default) =>
            Failure is not null ? Task.FromException<ImmichAsset>(Failure)
            : Asset is not null ? Task.FromResult(Asset)
            : Task.FromException<ImmichAsset>(new ImmichApiException("ImmichRequestFailed", "Immich returned HTTP 400 for GET /api/assets.", 400));
    }
}
