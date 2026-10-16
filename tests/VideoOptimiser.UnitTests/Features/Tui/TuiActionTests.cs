using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli.Features.Tui;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using static VideoOptimiser.UnitTests.Features.Tui.TuiTestData;

namespace VideoOptimiser.UnitTests.Features.Tui;

/// <summary>
/// What each effect does with the real Application services over a temporary database: the use case it calls, with what, and the
/// words it brings back. The worker, the launcher, the file opener and the finalisation are fakes that record their calls.
/// </summary>
public sealed class TuiActionTests : IDisposable
{
    private static readonly RemoteWorkerCreationOption Cx43 = new("cx43", "hel1", 0.0119m);

    private readonly TuiRig _rig = new();
    private readonly List<(LogKind Kind, string Text)> _progress = [];
    private readonly List<IDisposable> _locks = [];

    public void Dispose()
    {
        foreach (var held in _locks) held.Dispose();
        _rig.Dispose();
    }

    private void Log(LogKind kind, string text) => _progress.Add((kind, text));

    private static string[] Texts(ActionResult result) => [.. result.Lines.Select(line => line.Text)];

    private static QueueActionEffect Verb(QueueAction action, JobRecord job) => new(action, job.Id, Path.GetFileName(job.SourcePath));

    // ---- queue verbs ------------------------------------------------------------------------

    [Fact]
    public async Task HoldReleaseAndNextActThroughTheQueueVerbsOnTheJobAsItIsNow()
    {
        var job = await _rig.QueuedAsync("clip-a.mp4");

        var held = await _rig.Actions.QueueAsync(Verb(QueueAction.Hold, job), _rig.Settings, default);
        Texts(held).Should().Equal("Held clip-a.mp4. Runs pass over it until it's released.");
        (await _rig.StoredAsync(job)).Held.Should().BeTrue();

        Texts(await _rig.Actions.QueueAsync(Verb(QueueAction.Hold, job), _rig.Settings, default)).Should().Equal("clip-a.mp4 is already held.");

        Texts(await _rig.Actions.QueueAsync(Verb(QueueAction.Next, job), _rig.Settings, default)).Should().Equal("clip-a.mp4 runs next; it's no longer held.");
        var stored = await _rig.StoredAsync(job);
        (stored.Held, stored.Priority).Should().Be((false, 1));

        Texts(await _rig.Actions.QueueAsync(Verb(QueueAction.Release, job), _rig.Settings, default)).Should().Equal("clip-a.mp4 isn't held.");
        await _rig.Repository.SetHeldAsync(_rig.DatabasePath, stored, held: true);
        Texts(await _rig.Actions.QueueAsync(Verb(QueueAction.Release, job), _rig.Settings, default)).Should().Equal("Released clip-a.mp4; runs take it again.");
    }

    /// <summary>The verbs' refusals are the CLI's: a video that is ready is not held or run next, and the line says what it is.</summary>
    [Fact]
    public async Task AVerbOnAVideoThatIsNotWaitingSaysWhatItIs()
    {
        var ready = await _rig.ReadyAsync("clip-r.mp4");

        var result = await _rig.Actions.QueueAsync(Verb(QueueAction.Hold, ready), _rig.Settings, default);

        result.Lines.Should().ContainSingle().Which.Should().Be(new LogLine(LogKind.Warning, "Only waiting and stopped videos can be held or run next; clip-r.mp4 is ready to finalise."));
    }

    [Fact]
    public async Task DropLeavesTheQueueAndSaysWhatCleanupWillKeep()
    {
        var job = await _rig.QueuedAsync("clip-a.mp4");

        var result = await _rig.Actions.QueueAsync(Verb(QueueAction.Drop, job), _rig.Settings, default);

        Texts(result)[0].Should().Be("Dropped clip-a.mp4.");
        result.Notice.Should().Be("Dropped clip-a.mp4.");
        (await _rig.StoredAsync(job)).Status.Should().Be(JobStatus.Cancelled);
    }

    /// <summary>The refusal that protects an only copy keeps its meaning and its paths: a drop would let cleanup delete the AV1.</summary>
    [Fact]
    public async Task DropRefusesAVideoWhoseAv1MayBeTheOnlyCopyAndNamesIt()
    {
        var ready = await _rig.ReadyAsync("clip-r.mp4");
        File.Delete(ready.SourcePath);

        var result = await _rig.Actions.QueueAsync(Verb(QueueAction.Drop, ready), _rig.Settings, default);

        result.Lines.Should().ContainSingle().Which.Kind.Should().Be(LogKind.Warning);
        Texts(result)[0].Should().Be($"The original of clip-r.mp4 is missing, so its AV1 at {ready.OutputPath} may be the only copy. Put the original back, or move the AV1 somewhere safe, then drop the video.");
        (await _rig.StoredAsync(ready)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    /// <summary>A video converting cannot be held; the refusal names how to stop the run that has it.</summary>
    [Theory]
    [InlineData(LockHolders.BackgroundRun, "clip-c.mp4 is converting now (encoding). Press x to stop the run after this video, or X to stop it now.")]
    [InlineData(LockHolders.Run, "clip-c.mp4 is converting now (encoding). Stop the run with Ctrl+C in its window.")]
    public async Task AVerbOnAVideoConvertingNowNamesTheWayToStopTheRun(string holder, string expected)
    {
        var job = await _rig.QueuedAsync("clip-c.mp4");
        await _rig.MoveAsync(job, JobStatus.Encoding);
        _locks.Add(DatabaseLock.TryAcquire(DatabaseLock.RunPath(_rig.DatabasePath), holder, out _)!);

        var result = await _rig.Actions.QueueAsync(Verb(QueueAction.Hold, job), _rig.Settings, default);

        Texts(result).Should().Equal(expected);
    }

    [Fact]
    public async Task ADropWhileAFinaliseHoldsItsLockIsRefused()
    {
        var ready = await _rig.ReadyAsync("clip-r.mp4");
        var (held, _) = await DatabaseLock.AcquireFinalizeAsync(_rig.DatabasePath, "finalize", default);
        _locks.Add(held!);

        var result = await _rig.Actions.QueueAsync(Verb(QueueAction.Drop, ready), _rig.Settings, default);

        Texts(result).Should().Equal("A finalise is running on this database. Wait for it to finish, then drop clip-r.mp4.");
    }

    [Fact]
    public async Task AVideoThatLeftTheQueueIsSaidSoAndNothingIsCalled()
    {
        var job = await _rig.QueuedAsync("clip-a.mp4");
        await _rig.Actions.QueueAsync(Verb(QueueAction.Drop, job), _rig.Settings, default);

        var again = await _rig.Actions.QueueAsync(Verb(QueueAction.Drop, job), _rig.Settings, default);

        Texts(again).Should().Equal("clip-a.mp4 was already dropped, so there is nothing to drop.");
        var gone = await _rig.Actions.QueueAsync(new QueueActionEffect(QueueAction.Hold, Guid.NewGuid(), "ghost.mp4"), _rig.Settings, default);
        Texts(gone).Should().Equal("ghost.mp4 is not in the queue any more.");
    }

    /// <summary>VOPT-112: the reject key calls the queue verb the CLI calls; the AV1 is deleted, the original stays, and the video leaves the Review pane.</summary>
    [Fact]
    public async Task RejectDeletesTheAv1KeepsTheOriginalAndTheVideoLeavesTheReviewPane()
    {
        var ready = await _rig.ReadyAsync("clip-r.mp4", cleanable: true);
        var other = await _rig.ReadyAsync("clip-o.mp4", cleanable: true);

        var result = await _rig.Actions.QueueAsync(Verb(QueueAction.Reject, ready), _rig.Settings, default);

        result.Lines.Should().Equal(
            new LogLine(LogKind.Action, "Rejected clip-r.mp4. The original is untouched."),
            new LogLine(LogKind.Info, "Deleted its AV1."));
        result.Notice.Should().Be("Rejected clip-r.mp4.");
        File.Exists(ready.OutputPath!).Should().BeFalse();
        File.Exists(ready.OutputPath + ".manifest.json").Should().BeFalse();
        File.ReadAllText(ready.SourcePath).Should().Be("video bytes");
        File.Exists(other.OutputPath!).Should().BeTrue();
        (await _rig.StoredAsync(ready)).Status.Should().Be(JobStatus.Rejected);
        var snapshot = await _rig.Reader.ReadAsync(_rig.Settings, default);
        snapshot.Open.Should().ContainSingle().Which.Id.Should().Be(other.Id);
    }

    [Fact]
    public async Task AnAv1ThatCannotBeDeletedIsAWarningAndTheVideoStaysRejected()
    {
        var ready = await _rig.ReadyAsync("clip-r.mp4", cleanable: true);
        ActionResult result;
        using (new FileStream(ready.OutputPath!, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await _rig.Actions.QueueAsync(Verb(QueueAction.Reject, ready), _rig.Settings, default);
        }

        result.Lines.Should().Equal(
            new LogLine(LogKind.Action, "Rejected clip-r.mp4. The original is untouched."),
            new LogLine(LogKind.Warning, "Couldn't delete its AV1; it may be open in another program. It stays until cleanup deletes it."));
        (await _rig.StoredAsync(ready)).Status.Should().Be(JobStatus.Rejected);
    }

    /// <summary>The verb's refusals, worded for the screen: nothing is deleted and the video stays as it was.</summary>
    [Fact]
    public async Task RejectRefusesWhatTheVerbRefusesInTheScreensWords()
    {
        var waiting = await _rig.QueuedAsync("clip-q.mp4");
        var noOriginal = await _rig.ReadyAsync("clip-m.mp4", cleanable: true);
        File.Delete(noOriginal.SourcePath);
        var finalising = await _rig.ReadyAsync("clip-f.mp4", cleanable: true);

        Texts(await _rig.Actions.QueueAsync(Verb(QueueAction.Reject, waiting), _rig.Settings, default)).Should().Equal("Only a video that is ready to finalise can be rejected; clip-q.mp4 is waiting.");
        Texts(await _rig.Actions.QueueAsync(Verb(QueueAction.Reject, noOriginal), _rig.Settings, default)).Should().Equal($"The original of clip-m.mp4 is missing, so its AV1 at {noOriginal.OutputPath} may be the only copy. Put the original back, or move the AV1 somewhere safe, then reject the video.");
        var (held, _) = await DatabaseLock.AcquireFinalizeAsync(_rig.DatabasePath, "finalize", default);
        _locks.Add(held!);
        Texts(await _rig.Actions.QueueAsync(Verb(QueueAction.Reject, finalising), _rig.Settings, default)).Should().Equal("A finalise is running on this database. Wait for it to finish, then reject clip-f.mp4.");
        File.Exists(noOriginal.OutputPath!).Should().BeTrue();
        File.Exists(finalising.OutputPath!).Should().BeTrue();
        (await _rig.StoredAsync(noOriginal)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    [Fact]
    public async Task RejectingAVideoThatIsAlreadyRejectedSaysSo()
    {
        var ready = await _rig.ReadyAsync("clip-r.mp4", cleanable: true);
        await _rig.Actions.QueueAsync(Verb(QueueAction.Reject, ready), _rig.Settings, default);

        var again = await _rig.Actions.QueueAsync(Verb(QueueAction.Reject, ready), _rig.Settings, default);

        Texts(again).Should().Equal("clip-r.mp4 was already rejected, so there is nothing to reject.");
    }

    [Fact]
    public async Task RetryQueuesAFailedVideoAgainThroughTheRetryService()
    {
        var job = await _rig.QueuedAsync("clip-f.mp4");
        job.FailureCategory = "EncodeFailed";
        job.FailureMessage = "ab-av1 failed.";
        await _rig.MoveAsync(job, JobStatus.Encoding, JobStatus.Failed);

        var result = await _rig.Actions.QueueAsync(Verb(QueueAction.Retry, job), _rig.Settings, default);

        Texts(result)[0].Should().StartWith("Queued clip-f.mp4 again as job ");
        var open = await _rig.Repository.ListAsync(_rig.DatabasePath, terminal: false);
        open.Should().ContainSingle().Which.SourcePath.Should().Be(job.SourcePath);
    }

    [Fact]
    public async Task RetryOfAVideoThatCannotBeRetriedSaysWhyInTheErrorColour()
    {
        var waiting = await _rig.QueuedAsync("clip-a.mp4");

        var result = await _rig.Actions.QueueAsync(Verb(QueueAction.Retry, waiting), _rig.Settings, default);

        result.Lines.Should().ContainSingle().Which.Kind.Should().Be(LogKind.Error);
    }

    // ---- worker -----------------------------------------------------------------------------

    [Fact]
    public async Task TheServersAreListedThroughTheLifecycleInItsOrder()
    {
        _rig.Worker.CreationOptions = [Cx43, new("cx53", "fsn1", 0.0237m)];

        var msg = await _rig.Actions.LoadWorkerOptionsAsync(WorkerOptionsPurpose.Start, _rig.Settings, default);

        msg.Options.Should().Equal(_rig.Worker.CreationOptions);
        msg.Purpose.Should().Be(WorkerOptionsPurpose.Start);
        _rig.Worker.ListCalls.Should().Be(1);
    }

    [Fact]
    public async Task AListingThatFailsReturnsItsReasonAndCapacityKeepsItsWholeMessage()
    {
        _rig.Worker.ListFailure = new InvalidOperationException("No Hetzner API token is set.");
        (await _rig.Actions.LoadWorkerOptionsAsync(WorkerOptionsPurpose.Create, _rig.Settings, default)).Error.Should().Be("No Hetzner API token is set.");

        var capacity = new RemoteWorkerCapacityUnavailableException([new RemoteWorkerTypeOutcome("cx43", ["hel1"], "sold out")]);
        _rig.Worker.ListFailure = capacity;
        (await _rig.Actions.LoadWorkerOptionsAsync(WorkerOptionsPurpose.Create, _rig.Settings, default)).Error.Should().Be(capacity.Message);
    }

    [Fact]
    public async Task CreateMakesExactlyTheChosenServerLogsItsProgressAndForgetsTheStaleCheck()
    {
        _rig.Worker.Exists = false;

        var result = await _rig.Actions.CreateWorkerAsync(Cx43, _rig.Settings, Log, default);

        _rig.Worker.Created.Should().Equal(Cx43);
        _progress.Should().Equal((LogKind.Info, "Creating Hetzner cx43 in hel1."));
        Texts(result).Should().Equal("Created the worker video-optimiser-1 (cx43 in hel1). It is bootstrapping, and bills hourly until it is deleted.");
        result.RefreshWorker.Should().BeTrue();
    }

    [Fact]
    public async Task ACreateHetznerRefusesShowsItsReasonLineByLineAndNothingBills()
    {
        _rig.Worker.Exists = false;
        _rig.Worker.CreateFailure = new RemoteWorkerCapacityUnavailableException([new RemoteWorkerTypeOutcome("cx43", ["hel1"], "sold out")]);

        var result = await _rig.Actions.CreateWorkerAsync(Cx43, _rig.Settings, Log, default);

        result.Lines.Should().OnlyContain(line => line.Kind == LogKind.Error);
        Texts(result).Should().Contain("cx43 in hel1: sold out");
        result.Notice.Should().Be("No Hetzner worker could be created, so none is running. Tried:");
        _rig.Worker.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task AnyOtherCreateFailureIsOneErrorLine()
    {
        _rig.Worker.CreateFailure = new HttpRequestException("The network is down.\nSecond line.");

        var result = await _rig.Actions.CreateWorkerAsync(Cx43, _rig.Settings, Log, default);

        result.Lines.Should().ContainSingle().Which.Should().Be(new LogLine(LogKind.Error, "Couldn't create the worker: The network is down."));
    }

    [Fact]
    public async Task DeleteDeletesTheWorkerThroughTheLifecycleAndItsOwnLinesAreTheLog()
    {
        var result = await _rig.Actions.DeleteWorkerAsync(_rig.Settings, Log, default);

        _rig.Worker.DeleteCalls.Should().Be(1);
        _rig.Worker.Exists.Should().BeFalse();
        _progress.Select(line => line.Text).Should().Equal("Deleting the worker video-optimiser-1.", "Worker deleted; billing for it and its IPv4 address has stopped.");
        result.Notice.Should().Be("Deleted the worker.");
        result.RefreshWorker.Should().BeTrue();

        Texts(await _rig.Actions.DeleteWorkerAsync(_rig.Settings, Log, default)).Should().Equal("There was no worker to delete.");
    }

    [Fact]
    public async Task ADeleteThatFailsSaysThePriceOfLeavingTheWorkerUp()
    {
        _rig.Worker.DeleteFailure = new HttpRequestException("Hetzner answered 500.");

        var result = await _rig.Actions.DeleteWorkerAsync(_rig.Settings, Log, default);

        result.Lines.Select(line => (line.Kind, line.Text)).Should().Equal(
            (LogKind.Error, "Couldn't delete the worker: Hetzner answered 500."),
            (LogKind.Warning, "If it is still up, it bills hourly until it is deleted."));
    }

    // ---- starting a run ---------------------------------------------------------------------

    private void RunnerTakesTheLock() =>
        _rig.Launcher.OnLaunch = () => _locks.Add(DatabaseLock.TryAcquire(DatabaseLock.RunPath(_rig.DatabasePath), LockHolders.BackgroundRun, out _)!);

    private string[] LaunchedArguments => _rig.Launcher.Launches.Single().Arguments;

    /// <summary>The worker that is up is used: no create, the runner gets --remote and the fate chosen, and the loaded config's full path.</summary>
    [Fact]
    public async Task StartingWithTheWorkerThatIsUpLaunchesTheRunnerWithRemoteAndTheFate()
    {
        RunnerTakesTheLock();

        var result = await _rig.Actions.StartRunAsync(new StartPlan(null, WorkerFateChoice.Keep, ProcessingModeOverride.Remote), _rig.Settings, _rig.Configuration.Path, Log, default);

        LaunchedArguments.Should().Equal("background", "runner", "--worker-fate", "keep", "--config", Path.GetFullPath(_rig.Configuration.Path), "--remote");
        _rig.Launcher.Launches.Single().WorkingDirectory.Should().Be(Environment.CurrentDirectory);
        _rig.Worker.CreateCalls.Should().Be(0);
        Texts(result).Should().Equal($"Started a background run (process {Environment.ProcessId}). Closing this screen doesn't stop it.");
    }

    [Fact]
    public async Task StartingOnAServerChosenFromTheListCreatesItFirstAndThenLaunches()
    {
        _rig.Worker.Exists = false;
        RunnerTakesTheLock();

        var result = await _rig.Actions.StartRunAsync(new StartPlan(Cx43, WorkerFateChoice.Delete, ProcessingModeOverride.Remote), _rig.Settings, _rig.Configuration.Path, Log, default);

        _rig.Worker.Created.Should().Equal(Cx43);
        LaunchedArguments.Should().Equal("background", "runner", "--worker-fate", "delete", "--config", Path.GetFullPath(_rig.Configuration.Path), "--remote");
        _progress.Select(line => line.Text).Should().Equal("Creating Hetzner cx43 in hel1.", "Created the worker video-optimiser-1 (cx43 in hel1). It is bootstrapping.");
        result.RefreshWorker.Should().BeTrue();
    }

    [Fact]
    public async Task StartingWithNoWorkerLaunchesWithLocalAndCreatesNothing()
    {
        RunnerTakesTheLock();

        await _rig.Actions.StartRunAsync(new StartPlan(null, WorkerFateChoice.Delete, ProcessingModeOverride.Local), _rig.Settings, _rig.Configuration.Path, Log, default);

        LaunchedArguments.Should().Equal("background", "runner", "--worker-fate", "delete", "--config", Path.GetFullPath(_rig.Configuration.Path), "--local");
        _rig.Worker.CreateCalls.Should().Be(0);
    }

    [Fact]
    public async Task StartingWithoutAManagedWorkerPassesNoModeFlagSoTheConfigsModeStands()
    {
        RunnerTakesTheLock();

        await _rig.Actions.StartRunAsync(new StartPlan(null, WorkerFateChoice.Delete, null), _rig.Settings, _rig.Configuration.Path, Log, default);

        LaunchedArguments.Should().Equal("background", "runner", "--worker-fate", "delete", "--config", Path.GetFullPath(_rig.Configuration.Path));
    }

    /// <summary>A run without its worker would only fail, and a launch after a failed create would bill nothing but start nothing useful.</summary>
    [Fact]
    public async Task AFailedCreateLaunchesNothingAndSaysSo()
    {
        _rig.Worker.Exists = false;
        _rig.Worker.CreateFailure = new RemoteWorkerCapacityUnavailableException([new RemoteWorkerTypeOutcome("cx43", ["hel1"], "sold out")]);

        var result = await _rig.Actions.StartRunAsync(new StartPlan(Cx43, WorkerFateChoice.Delete, ProcessingModeOverride.Remote), _rig.Settings, _rig.Configuration.Path, Log, default);

        _rig.Launcher.Launches.Should().BeEmpty();
        Texts(result).Should().Contain("cx43 in hel1: sold out").And.EndWith("No run was started.");
    }

    [Fact]
    public async Task ARunnerThatEndsAtOnceIsSaidWithItsExitCodeAndAServerMadeForItIsFlagged()
    {
        _rig.Worker.Exists = false;
        _rig.Launcher.ExitCode = 3;

        var result = await _rig.Actions.StartRunAsync(new StartPlan(Cx43, WorkerFateChoice.Delete, ProcessingModeOverride.Remote), _rig.Settings, _rig.Configuration.Path, Log, default);

        Texts(result).Should().Equal(
            "The runner ended at once with exit code 3, so no run started.",
            "The worker video-optimiser-1 is up and bills hourly until it is deleted: delete it in the Worker pane, or start the run again to use it.");
    }

    [Fact]
    public async Task ARunAlreadyGoingIsSaidAndNothingIsLaunched()
    {
        _locks.Add(DatabaseLock.TryAcquire(DatabaseLock.RunPath(_rig.DatabasePath), LockHolders.Run, out _)!);

        var result = await _rig.Actions.StartRunAsync(new StartPlan(null, WorkerFateChoice.Delete, null), _rig.Settings, _rig.Configuration.Path, Log, default);

        _rig.Launcher.Launches.Should().BeEmpty();
        Texts(result)[0].Should().StartWith("A run is already going: started ").And.EndWith($"(process {Environment.ProcessId}).");
    }

    // ---- stopping ---------------------------------------------------------------------------

    [Fact]
    public async Task StopAsksABackgroundRunThroughTheRequestsRowAndTheNowFormSaysWhatItCosts()
    {
        var store = new SqliteRunRequestStore(new SqliteDatabaseInitializer());
        await store.ResetAsync(_rig.DatabasePath, WorkerFateChoice.Delete, default);
        _locks.Add(DatabaseLock.TryAcquire(DatabaseLock.RunPath(_rig.DatabasePath), LockHolders.BackgroundRun, out _)!);

        Texts(await _rig.Actions.StopAsync(now: false, _rig.Settings, default)).Should().Equal("Asked the run to stop after this video.");
        (await store.ReadAsync(_rig.DatabasePath, default)).Should().Be(new RunRequests(true, false, WorkerFateChoice.Delete));

        Texts(await _rig.Actions.StopAsync(now: true, _rig.Settings, default)).Should().Equal("Asked the run to stop now. The video converting now starts again from its stage next time.");
        (await store.ReadAsync(_rig.DatabasePath, default)).StopNow.Should().BeTrue();
    }

    /// <summary>A foreground run never reads the requests row, so asking it would be silently ignored: the line names the window instead.</summary>
    [Fact]
    public async Task StopOfAForegroundRunAsksNothingAndSaysOnlyCtrlCThereStopsIt()
    {
        _locks.Add(DatabaseLock.TryAcquire(DatabaseLock.RunPath(_rig.DatabasePath), LockHolders.Run, out _)!);

        var result = await _rig.Actions.StopAsync(now: false, _rig.Settings, default);

        Texts(result).Should().Equal("That run is in another window (video-optimiser run); only Ctrl+C there stops it.");
    }

    [Fact]
    public async Task StopWithNoRunSaysSo()
    {
        Texts(await _rig.Actions.StopAsync(now: false, _rig.Settings, default)).Should().Equal("No run is going.");
    }

    [Fact]
    public async Task ChangingTheFateWritesItInTheRequestsRowForTheRunToReadWhenItEnds()
    {
        var store = new SqliteRunRequestStore(new SqliteDatabaseInitializer());
        await store.ResetAsync(_rig.DatabasePath, WorkerFateChoice.Delete, default);

        var result = await _rig.Actions.SetWorkerFateAsync(WorkerFateChoice.Keep, _rig.Settings, default);

        (await store.ReadAsync(_rig.DatabasePath, default)).WorkerFate.Should().Be(WorkerFateChoice.Keep);
        Texts(result).Should().Equal("The worker will stay up when the run ends, billed hourly.");
    }

    // ---- opening ----------------------------------------------------------------------------

    [Fact]
    public async Task OpenLaunchesTheOriginalOrTheAv1InTheDefaultApp()
    {
        var ready = await _rig.ReadyAsync("clip-r.mp4");

        var original = await _rig.Actions.OpenAsync(new OpenFile(ready.Id, Original: true, "clip-r.mp4"), _rig.Settings, default);
        var av1 = await _rig.Actions.OpenAsync(new OpenFile(ready.Id, Original: false, "clip-r.mp4"), _rig.Settings, default);

        _rig.Opener.Opened.Should().Equal(ready.SourcePath, ready.OutputPath);
        original.Notice.Should().Be("Opened the original of clip-r.mp4.");
        av1.Notice.Should().Be("Opened the AV1 of clip-r.mp4.");
    }

    /// <summary>An Immich video's original is its downloaded copy, and when that is gone a run downloads it again.</summary>
    [Fact]
    public async Task OpenOfAMissingFileSaysSoAndLaunchesNothing()
    {
        var ready = await _rig.ReadyAsync("clip-r.mp4");
        File.Delete(ready.OutputPath!);
        var immich = await _rig.ReadyAsync("clip-i.mp4", immich: true);
        File.Delete(immich.SourcePath);

        Texts(await _rig.Actions.OpenAsync(new OpenFile(ready.Id, false, "clip-r.mp4"), _rig.Settings, default)).Should().Equal($"The AV1 of clip-r.mp4 is missing: {ready.OutputPath}");
        Texts(await _rig.Actions.OpenAsync(new OpenFile(immich.Id, true, "clip-i.mp4"), _rig.Settings, default)).Should().Equal($"The downloaded copy of clip-i.mp4 is missing: {immich.SourcePath}");
        _rig.Opener.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOpenerThatFailsIsAnErrorLineAndNotACrash()
    {
        var ready = await _rig.ReadyAsync("clip-r.mp4");
        _rig.Opener.Failure = new InvalidOperationException("No app is set for this file type.");

        var result = await _rig.Actions.OpenAsync(new OpenFile(ready.Id, true, "clip-r.mp4"), _rig.Settings, default);

        result.Lines.Should().ContainSingle().Which.Should().Be(new LogLine(LogKind.Error, "Couldn't open the original of clip-r.mp4: No app is set for this file type."));
    }

    // ---- finalising -------------------------------------------------------------------------

    /// <summary>The confirmation is the one <c>finalize --ready</c> shows: a line per video, what happens to the originals, and the Immich line.</summary>
    [Fact]
    public async Task TheFinaliseConfirmationShowsWhatFinalizeShowsForEveryReadyVideo()
    {
        var first = await _rig.ReadyAsync("clip-a.mov");
        var second = await _rig.ReadyAsync("clip-b.mp4");
        var immich = await _rig.ReadyAsync("clip-i.mp4", immich: true);
        await _rig.QueuedAsync("clip-q.mp4");

        var prepared = await _rig.Actions.PrepareFinaliseAsync(new PrepareFinalise(null), _rig.Settings, default);

        prepared.Key.Should().Be("finalise");
        prepared.Confirm!.Question.Should().Be("Finalise 3 videos?");
        prepared.Confirm.Detail.Split('\n').Should().Equal(
            "clip-a.mov  1000 B -> 400 B, 60.0% saved",
            "clip-b.mp4  1000 B -> 400 B, 60.0% saved",
            "clip-i.mp4  1000 B -> 400 B, 60.0% saved",
            "Each original is deleted and its AV1 takes its place as an .mp4 file.",
            "Each Immich original goes to Immich's trash once its AV1 is uploaded with its albums, tags and other relations.");
        prepared.Confirm.OnYes.Should().BeOfType<FinaliseVideos>().Which.Ids.Should().Equal(first.Id, second.Id, immich.Id);
    }

    [Fact]
    public async Task TheConfirmationForOneVideoNamesItAndCoversOnlyIt()
    {
        await _rig.ReadyAsync("clip-a.mp4");
        var second = await _rig.ReadyAsync("clip-b.mp4");

        var prepared = await _rig.Actions.PrepareFinaliseAsync(new PrepareFinalise(second.Id), _rig.Settings, default);

        prepared.Confirm!.Question.Should().Be("Finalise clip-b.mp4?");
        prepared.Confirm.OnYes.Should().BeOfType<FinaliseVideos>().Which.Ids.Should().Equal(second.Id);
    }

    /// <summary>The archive action's line carries its folder, and a keep action says the AV1 goes beside the original: both are what finalize says.</summary>
    [Theory]
    [InlineData("keep", "Each original stays; its AV1 is installed beside it as <name>.av1<extension>.")]
    [InlineData("archive", "Each original moves to an archive folder beside it and its AV1 takes its place as an .mp4 file.")]
    public async Task TheConfirmationSaysWhatTheConfiguredOriginalActionDoes(string action, string line)
    {
        _rig.Settings.Original.Action = action;
        _rig.Settings.Original.ArchiveDirectory = "archive";
        await _rig.ReadyAsync("clip-a.mp4");

        var prepared = await _rig.Actions.PrepareFinaliseAsync(new PrepareFinalise(null), _rig.Settings, default);

        prepared.Confirm!.Detail.Split('\n').Should().Contain(line);
    }

    [Fact]
    public async Task TheConfirmationListsSixVideosAndCountsTheRest()
    {
        for (var number = 1; number <= 8; number++) await _rig.ReadyAsync($"clip-{number}.mp4");

        var prepared = await _rig.Actions.PrepareFinaliseAsync(new PrepareFinalise(null), _rig.Settings, default);

        prepared.Confirm!.Question.Should().Be("Finalise 8 videos?");
        var lines = prepared.Confirm.Detail.Split('\n');
        lines.Should().HaveCount(8).And.Contain("And 2 more.");
        prepared.Confirm.OnYes.Should().BeOfType<FinaliseVideos>().Which.Ids.Should().HaveCount(8);
    }

    [Fact]
    public async Task NothingReadyAndAVideoThatIsNotReadyEachGiveANoticeAndNoDialog()
    {
        var waiting = await _rig.QueuedAsync("clip-q.mp4");

        (await _rig.Actions.PrepareFinaliseAsync(new PrepareFinalise(null), _rig.Settings, default)).Should().Be(new PreparedMsg("finalise", null, "Nothing is ready to finalise."));
        (await _rig.Actions.PrepareFinaliseAsync(new PrepareFinalise(waiting.Id), _rig.Settings, default)).Should().Be(new PreparedMsg("finalise", null, "That video can't be finalised now."));
    }

    [Fact]
    public async Task FinaliseHoldsTheFinaliseLockFinalisesEachVideoInOrderAndLogsOneLineEachAndASummary()
    {
        var first = await _rig.ReadyAsync("clip-a.mp4");
        var second = await _rig.ReadyAsync("clip-b.mp4");
        _rig.Finalizer.Results[second.Id] = new FinalizationResult(null, ExitCode.FinalisationFailure, "The original changed since it was converted.");

        var result = await _rig.Actions.FinaliseAsync(new FinaliseVideos([first.Id, second.Id]), _rig.Settings, default);

        _rig.Finalizer.Finalised.Should().Equal(first.Id, second.Id);
        result.Lines.Select(line => (line.Kind, line.Text)).Should().Equal(
            (LogKind.Success, "Finalised clip-a.mp4: Original deleted; the AV1 is in its place."),
            (LogKind.Error, "Couldn't finalise clip-b.mp4: The original changed since it was converted."),
            (LogKind.Warning, "Finalised 1; 1 failed."));
        result.Notice.Should().Be("Finalised 1; 1 failed.");
    }

    [Fact]
    public async Task AFinaliseThatThrowsIsALineAndTheNextVideoStillGoes()
    {
        var first = await _rig.ReadyAsync("clip-a.mp4");
        _rig.Finalizer.Failure = new IOException("The file is in use.");

        var result = await _rig.Actions.FinaliseAsync(new FinaliseVideos([first.Id, Id(99)]), _rig.Settings, default);

        _rig.Finalizer.Finalised.Should().HaveCount(2);
        result.Lines.Should().Contain(line => line.Kind == LogKind.Error && line.Text == "Couldn't finalise clip-a.mp4: The file is in use.");
    }

    [Fact]
    public async Task AFinaliseWhileAnotherHoldsTheLockFinalisesNothingAndSaysSo()
    {
        var ready = await _rig.ReadyAsync("clip-a.mp4");
        var (held, _) = await DatabaseLock.AcquireFinalizeAsync(_rig.DatabasePath, "finalize", default);
        _locks.Add(held!);

        var result = await _rig.Actions.FinaliseAsync(new FinaliseVideos([ready.Id]), _rig.Settings, default);

        _rig.Finalizer.Finalised.Should().BeEmpty();
        Texts(result).Should().Equal("Another finalise is running on this database, so nothing was finalised. Wait for it to finish.");
    }
}
