using FluentAssertions;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli.Features.Tui;
using static VideoOptimiser.UnitTests.Features.Tui.TuiTestData;

namespace VideoOptimiser.UnitTests.Features.Tui;

/// <summary>Update is pure: each test replays messages over a model and reads the result.</summary>
public sealed class TuiUpdateTests
{
    // ---- snapshots --------------------------------------------------------------------------

    /// <summary>The cursor is on a job, not a row: a refresh that reorders the queue must not move it to another video.</summary>
    [Fact]
    public void ASnapshotReplacesTheVideosAndKeepsTheSelectionOnTheSameJob()
    {
        var model = After(Model(Queued(1), Queued(2), Queued(3)), Snap(Quiet([Queued(1), Queued(2), Queued(3)])), Press(ConsoleKey.DownArrow));
        model.QueueSelection.Should().Be(Id(2));

        // The second video starts converting, so it moves to the top group.
        model = After(model, Snap(Quiet([Queued(1), Converting(2), Queued(3)])));

        model.Ordered().Select(video => video.Id).Should().Equal(Id(2), Id(1), Id(3));
        model.QueueSelection.Should().Be(Id(2));
    }

    /// <summary>The video under the cursor leaves the queue (dropped from another terminal): the cursor lands on a neighbour, the next one first.</summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    public void TheSelectionMovesToANeighbourWhenItsVideoIsGone(int gone, int expected)
    {
        Video[] all = [Queued(1), Queued(2), Queued(3)];
        var model = After(Model(), Snap(Quiet(all))) with { QueueSelection = Id(gone) };

        model = After(model, Snap(Quiet(all.Where(video => video.Id != Id(gone)))));

        model.QueueSelection.Should().Be(Id(expected));
    }

    [Fact]
    public void ASnapshotWithNothingSelectedSelectsTheFirstVideoAndTheFirstReadyOne()
    {
        var model = After(Model(), Snap(Quiet([Queued(1), Ready(2), Ready(3)])));

        model.QueueSelection.Should().Be(Id(1));
        model.ReviewSelection.Should().Be(Id(2));
    }

    [Fact]
    public void AnEmptySnapshotClearsTheSelections()
    {
        var model = After(Model(), Snap(Quiet([Queued(1), Ready(2)])), Snap(Quiet([])));

        model.QueueSelection.Should().BeNull();
        model.ReviewSelection.Should().BeNull();
    }

    /// <summary>A database locked for a moment must not blank the queue: the section that threw keeps its data and a line says so.</summary>
    [Fact]
    public void ASectionThatFailedToReadKeepsItsDataAndShowsOneProblemUntilTheNextGoodRead()
    {
        var good = Quiet([Queued(1), Queued(2)], [Failed(7)]);
        var model = After(Model(), Snap(good));

        model = After(model, Snap(Snapshot(open: null, failed: null, run: new RunSection(null, null), worker: good.Worker, events: [], "Couldn't read the queue: database is locked.")));

        model.Open.Should().HaveCount(2);
        model.Failed.Should().HaveCount(1);
        model.ReadProblem.Should().Be("Couldn't read the queue: database is locked.");

        model = After(model, Snap(good));
        model.ReadProblem.Should().BeNull();
    }

    [Fact]
    public void TheRunAndItsRequestsFollowTheSnapshotAndClearWhenTheRunEnds()
    {
        var holder = new LiveRunHolder(LockHolders.BackgroundRun, Now, 4242);
        var requests = new RunRequests(StopAfterThisVideo: true, StopNow: false, WorkerFate: WorkerFateChoice.Delete);

        var model = After(Model(), Snap(Snapshot([], [], new RunSection(holder, requests), new WorkerSection(false, "manual", null), [])));
        model.Run.Should().Be(holder);
        model.Requests.Should().Be(requests);

        model = After(model, Snap(Quiet([])));
        model.Run.Should().BeNull();
        model.Requests.Should().BeNull();
    }

    // ---- the event file ---------------------------------------------------------------------

    [Fact]
    public void NewEventsAppendLogLinesAndAnEventAlreadyShownIsNotShownAgain()
    {
        RunEvent[] first = [Started(), Event(RunEventKind.VideosHeld, count: 2)];
        var model = After(Model(), Snap(Snapshot([], [], new RunSection(null, null), null, first)));
        model.Log.Should().HaveCount(2);

        RunEvent[] more = [.. first, Event(RunEventKind.ConversionStarted, count: 3)];
        model = After(model, Snap(Snapshot([], [], new RunSection(null, null), null, more)), Snap(Snapshot([], [], new RunSection(null, null), null, more)));

        model.Log.Select(entry => entry.Text).Should().Equal(
            "Background run started (process 4242).",
            "2 videos are held; the run passes over them.",
            "Converting 3 videos.");
    }

    /// <summary>The fate means something only when there is a managed worker to delete or keep; with none, the line says nothing of it.</summary>
    [Theory]
    [InlineData(true, "Background run started (process 4242). The worker is deleted when the run ends.")]
    [InlineData(false, "Background run started (process 4242).")]
    public void TheRunStartedLineMentionsTheWorkerFateOnlyWithAManagedWorker(bool managed, string expected)
    {
        var worker = new WorkerSection(managed, managed ? "hetzner" : "manual", null);

        var model = After(Model(), Snap(Snapshot([], [], new RunSection(null, null), worker, [Started()])));

        model.Log.Should().ContainSingle().Which.Text.Should().Be(expected);
    }

    /// <summary>A reopened TUI reads the file from the start, so it catches up on a run it never saw.</summary>
    [Fact]
    public void AFirstSnapshotTurnsTheWholeFileIntoLogLines()
    {
        RunEvent[] events = [Started(), Event(RunEventKind.ConversionStarted, count: 1), Progress(JobProgressKind.Header, "[1/1] clip-001.mp4 (1.1 GiB)"), Progress(JobProgressKind.Step, "Finding the CRF.")];

        var model = After(Model(), Snap(Snapshot([], [], new RunSection(null, null), null, events)));

        model.Log.Should().HaveCount(4);
        model.Activity.EventsSeen.Should().Be(4);
    }

    /// <summary>The next run replaces the file: a shorter one, or another RunStarted, is not more of the same run.</summary>
    [Theory]
    [InlineData("shorter file")]
    [InlineData("another process")]
    [InlineData("another start time")]
    public void ANewRunResetsTheLog(string how)
    {
        RunEvent[] firstRun = [Started(processId: 100, secondsAfterNow: 0), Event(RunEventKind.VideosHeld, count: 1), Event(RunEventKind.ConversionStarted, count: 1)];
        var model = After(Model(), Snap(Snapshot([], [], new RunSection(null, null), null, firstRun)));
        model.Log.Should().HaveCount(3);

        RunEvent[] secondRun = how switch
        {
            "shorter file" => [Started(processId: 100)],
            "another process" => [Started(processId: 200), Event(RunEventKind.VideosHeld, count: 1), Event(RunEventKind.VideosHeld, count: 2), Event(RunEventKind.VideosHeld, count: 3)],
            _ => [Started(processId: 100, secondsAfterNow: 60), Event(RunEventKind.VideosHeld, count: 1), Event(RunEventKind.VideosHeld, count: 2), Event(RunEventKind.VideosHeld, count: 3)]
        };
        model = After(model, Snap(Snapshot([], [], new RunSection(null, null), null, secondRun)));

        model.Log.Should().HaveCount(secondRun.Length);
        model.Log[0].Text.Should().StartWith("Background run started");
        model.Activity.EventsSeen.Should().Be(secondRun.Length);
    }

    /// <summary>
    /// A reading repeats every few seconds, so the pane shows the latest one with its bar and the log never sees it; a CRF
    /// attempt is a fact the user reads back, so it is logged under its step and also stands as the progress line.
    /// </summary>
    [Fact]
    public void ReadingsAreOnlyTheProgressLineAndCrfAttemptsAreAlsoLogged()
    {
        RunEvent[] events =
        [
            Started(),
            Progress(JobProgressKind.Header, "[1/2] clip-001.mp4 (1.1 GiB)"),
            Progress(JobProgressKind.Step, "Finding the best CRF."),
            Progress(JobProgressKind.Detail, "CRF 18   VMAF 97.24   size  87%   passes, file too big"),
        ];

        var model = After(Model(), Snap(Snapshot([], [], new RunSection(null, null), null, events)));

        model.Log.Select(entry => (entry.Kind, entry.Text)).Should().Equal(
            (LogKind.Action, "Background run started (process 4242)."),
            (LogKind.Stage, "[1/2] clip-001.mp4 (1.1 GiB)"),
            (LogKind.Info, "Finding the best CRF."),
            (LogKind.Detail, "CRF 18   VMAF 97.24   size  87%   passes, file too big"));
        model.Activity.Should().Match<ActivityState>(state => state.ProgressText == "CRF 18   VMAF 97.24   size  87%   passes, file too big" && state.ProgressPercent == null);

        // A step starts new work: the attempt's line goes until the first reading.
        RunEvent[] encoding = [.. events, Progress(JobProgressKind.Step, "Encoding at CRF 19.")];
        model = After(model, Snap(Snapshot([], [], new RunSection(null, null), null, encoding)));
        model.Activity.ProgressText.Should().BeNull();

        RunEvent[] readings =
        [
            .. encoding,
            Progress(JobProgressKind.Detail, "12%, 41 fps, about 7m 00s left", percent: 12),
            Progress(JobProgressKind.Detail, "14%, 42 fps, about 6m 00s left", percent: 14),
        ];
        model = After(model, Snap(Snapshot([], [], new RunSection(null, null), null, readings)));
        model.Log[^1].Text.Should().Be("Encoding at CRF 19.");
        model.Activity.Should().Match<ActivityState>(state => state.ProgressText == "14%, 42 fps, about 6m 00s left" && state.ProgressPercent == 14);

        // The video's outcome ends its progress line.
        RunEvent[] more = [.. readings, Progress(JobProgressKind.Step, "Ready to finalise: 1.1 GiB -> 400.0 MiB, 65.0% saved.", JobTone.Success)];
        model = After(model, Snap(Snapshot([], [], new RunSection(null, null), null, more)));
        model.Activity.Should().Match<ActivityState>(state => state.ProgressText == null && state.ProgressPercent == null);
        model.Log[^1].Kind.Should().Be(LogKind.Success);
    }

    /// <summary>
    /// A status (the test clip ab-av1 is on) only replaces the progress line; an SSH resend is logged as a warning, since it
    /// explains a stall; the worker answering a poll is not news, so it logs nothing and does not end a quiet spell.
    /// </summary>
    [Fact]
    public void StatusesStandAsTheProgressLineResendsAreWarningsAndAnswersOnlyKeepTheirTime()
    {
        RunEvent[] events =
        [
            Started(),
            Progress(JobProgressKind.Header, "[1/2] clip-001.mp4 (1.1 GiB)"),
            Progress(JobProgressKind.Step, "Finding the best CRF."),
            Progress(JobProgressKind.Status, "CRF 34: encoding test clip 1 of 2.", secondsAfterNow: 5),
            new(Now.AddSeconds(40), RunEventKind.Progress, Text: string.Empty, Progress: nameof(JobProgressKind.WorkerAnswered), Tone: nameof(JobTone.None)),
        ];

        var model = After(Model(), Snap(Snapshot([], [], new RunSection(null, null), null, events)));

        model.Log[^1].Text.Should().Be("Finding the best CRF.");
        model.Activity.Should().Match<ActivityState>(state => state.ProgressText == "CRF 34: encoding test clip 1 of 2." && state.ProgressPercent == null
            && state.LastEventAt == Now.AddSeconds(5) && state.LastAnswerAt == Now.AddSeconds(40));

        RunEvent[] resent = [.. events, Progress(JobProgressKind.Detail, "The worker didn't answer within 30s; asking again (try 2 of 3).", JobTone.Warning, secondsAfterNow: 75)];
        model = After(model, Snap(Snapshot([], [], new RunSection(null, null), null, resent)));
        model.Log[^1].Should().Match<LogEntry>(entry => entry.Kind == LogKind.Warning && entry.Text == "The worker didn't answer within 30s; asking again (try 2 of 3).");
        model.Activity.ProgressText.Should().Be("The worker didn't answer within 30s; asking again (try 2 of 3).");
    }

    [Theory]
    [InlineData(JobTone.Success, "Success")]
    [InlineData(JobTone.Warning, "Warning")]
    [InlineData(JobTone.Failure, "Error")]
    [InlineData(JobTone.None, "Info")]
    public void AStepLineTakesItsColourFromItsTone(JobTone tone, string expected)
    {
        var model = After(Model(), Snap(Snapshot([], [], new RunSection(null, null), null, [Started(), Progress(JobProgressKind.Step, "A step.", tone)])));

        model.Log[^1].Kind.ToString().Should().Be(expected);
    }

    [Fact]
    public void ARunsPhaseFollowsItsEventsSoTheIdleTextCanSayWhyNothingConverts()
    {
        ActivityState Fold(params RunEvent[] events) => After(Model(), Snap(Snapshot([], [], new RunSection(null, null), null, events))).Activity;

        Fold(Started()).Phase.Should().Be(RunPhase.Preparing);
        Fold(Started(), Event(RunEventKind.WorkerProgress, text: "Creating Hetzner cx43 in hel1.")).WaitingForWorker.Should().BeTrue();
        Fold(Started(), Event(RunEventKind.WorkerProgress, text: "ready"), Event(RunEventKind.ConversionStarted, count: 1)).Should().Match<ActivityState>(state => state.Phase == RunPhase.Converting && !state.WaitingForWorker);
        // The worker's delete at the end reports progress too, and the run is no longer waiting for it.
        Fold(Started(), Event(RunEventKind.ConversionStarted, count: 1), Event(RunEventKind.ConversionEnded), Event(RunEventKind.WorkerProgress, text: "Deleting the worker.")).Should().Match<ActivityState>(state => state.Phase == RunPhase.Ending && !state.WaitingForWorker);
    }

    [Fact]
    public void TheLogKeepsTheLatestFourHundredLinesAndAScrolledPaneStaysOnTheSameLines()
    {
        var events = new List<RunEvent> { Started() };
        events.AddRange(Enumerable.Range(1, 500).Select(n => Event(RunEventKind.VideosHeld, count: n)));
        var model = After(Model(), Snap(Snapshot([], [], new RunSection(null, null), null, events)));
        model.Log.Should().HaveCount(400);
        model.Log[^1].Text.Should().Be("500 videos are held; the run passes over them.");

        model = After(model with { LogScroll = 3 }, Snap(Snapshot([], [], new RunSection(null, null), null, [.. events, Event(RunEventKind.VideosHeld, count: 501)])));
        model.LogScroll.Should().Be(4);
    }

    // ---- keys -------------------------------------------------------------------------------

    [Theory]
    [InlineData('q')]
    [InlineData('Q')]
    public void QQuits(char key) => After(Model(), Letter(key)).Quit.Should().BeTrue();

    [Fact]
    public void CtrlCQuitsAsAKeyAndEvenOverADialog()
    {
        var ctrlC = Press(ConsoleKey.C, '\u0003', ConsoleModifiers.Control);

        After(Model(), ctrlC).Quit.Should().BeTrue();
        After(Model() with { Confirm = new TuiConfirm("Sure?", "It matters.", new RefreshWorkerCheck(0)) }, ctrlC).Quit.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, "Worker")]
    [InlineData(true, "Review")]
    public void TabMovesTheFocusForwardAndShiftTabBackWrappingAtTheEnds(bool shift, string expected)
    {
        var start = Model() with { Focus = Pane.Queue };

        var model = After(start, Press(ConsoleKey.Tab, '\t', shift ? ConsoleModifiers.Shift : 0));

        model.Focus.ToString().Should().Be(expected);
    }

    /// <summary>The queue pane's rows run converting, queue, held, ready, failed; the arrows walk them in that order, not in run order of the list.</summary>
    [Fact]
    public void ArrowsMoveTheQueueSelectionThroughTheGroupsInDisplayOrderAndStopAtTheEnds()
    {
        var model = After(Model(), Snap(Quiet([Ready(5), Held(4), Queued(2), Converting(1)], [Failed(9)])));
        model.QueueSelection.Should().Be(Id(1));

        model = After(model, Press(ConsoleKey.DownArrow), Press(ConsoleKey.DownArrow), Press(ConsoleKey.DownArrow));
        model.QueueSelection.Should().Be(Id(5));

        model = After(model, Press(ConsoleKey.End));
        model.QueueSelection.Should().Be(Id(9));
        After(model, Press(ConsoleKey.DownArrow)).QueueSelection.Should().Be(Id(9));

        After(model, Press(ConsoleKey.Home)).QueueSelection.Should().Be(Id(1));
        After(model, Press(ConsoleKey.UpArrow)).QueueSelection.Should().Be(Id(5));
    }

    [Fact]
    public void ArrowsInTheReviewPaneMoveOnlyThroughTheReadyVideos()
    {
        var model = After(Model(), Snap(Quiet([Queued(1), Ready(2), Ready(3), Ready(4)])), Press(ConsoleKey.Tab), Press(ConsoleKey.Tab), Press(ConsoleKey.Tab));
        model.Focus.Should().Be(Pane.Review);
        model.ReviewSelection.Should().Be(Id(2));

        model = After(model, Press(ConsoleKey.DownArrow));

        model.ReviewSelection.Should().Be(Id(3));
        model.QueueSelection.Should().Be(Id(1));
    }

    [Fact]
    public void ArrowsInTheActivityPaneScrollTheLogAndEndFollowsIt()
    {
        var events = new List<RunEvent> { Started() };
        events.AddRange(Enumerable.Range(1, 30).Select(n => Event(RunEventKind.VideosHeld, count: n)));
        var model = After(Model() with { Focus = Pane.Activity }, Snap(Snapshot([], [], new RunSection(null, null), null, events)));

        After(model, Press(ConsoleKey.UpArrow)).LogScroll.Should().Be(1);
        After(model, Press(ConsoleKey.PageUp)).LogScroll.Should().Be(10);
        After(model, Press(ConsoleKey.Home)).LogScroll.Should().Be(30);
        After(model with { LogScroll = 7 }, Press(ConsoleKey.End)).LogScroll.Should().Be(0);
        After(model, Press(ConsoleKey.DownArrow)).LogScroll.Should().Be(0);
    }

    [Fact]
    public void ADialogConfirmsOnlyOnAnExplicitYAndAnythingElseCancelsIt()
    {
        var asking = Model() with { Confirm = new TuiConfirm("Check the worker?", "It asks Hetzner.", new RefreshWorkerCheck(3)) };

        var yes = Update(asking, Letter('y'));
        yes.Model.Confirm.Should().BeNull();
        yes.Effects.Should().Equal(new RefreshWorkerCheck(3));

        foreach (var key in new[] { Letter('n'), Press(ConsoleKey.Enter, '\r'), Press(ConsoleKey.Escape, '\u001b'), Letter('q') })
        {
            var no = Update(asking, key);
            no.Model.Confirm.Should().BeNull();
            no.Effects.Should().BeEmpty();
            no.Model.Quit.Should().BeFalse();
        }
    }

    // ---- the worker -------------------------------------------------------------------------

    private static TuiModel ManagedWithWorker(RemoteWorkerInfo? recorded = null) =>
        After(Model(), Snap(Snapshot([], [], new RunSection(null, null), new WorkerSection(true, "hetzner", recorded), [])));

    /// <summary>Asking Hetzner takes up to 15 seconds, so the question goes out as an effect, once, and again every 30 seconds.</summary>
    [Fact]
    public void ATickAsksHetznerAboutAManagedWorkerAtOnceThenEveryThirtySeconds()
    {
        var model = ManagedWithWorker(AWorker());

        var first = Update(model, Tick(TimeSpan.FromSeconds(1)));
        first.Effects.Should().Equal(new RefreshWorkerCheck(0));

        var whileAsking = Update(first.Model, Tick(TimeSpan.FromSeconds(20)));
        whileAsking.Effects.Should().BeEmpty();

        var answered = After(first.Model, new WorkerCheckedMsg(0, new WorkerCheck(AWorker(status: "running"), 0.0119m, null)), Tick(TimeSpan.FromSeconds(10)));
        Update(answered, Tick(TimeSpan.FromSeconds(30))).Effects.Should().BeEmpty();
        Update(answered, Tick(TimeSpan.FromSeconds(32))).Effects.Should().Equal(new RefreshWorkerCheck(0));
    }

    [Fact]
    public void ATickNeverAsksHetznerWhenTheLifecycleIsNotManaged()
    {
        var model = After(Model(), Snap(Quiet([])));

        Update(model, Tick(TimeSpan.FromSeconds(1))).Effects.Should().BeEmpty();
    }

    /// <summary>A worker created or deleted while a question was out makes its answer about another server: it is dropped and the question goes out again.</summary>
    [Fact]
    public void AnAnswerAboutAnEarlierWorkerIsDroppedWhenTheRecordedWorkerChanged()
    {
        var asked = Update(ManagedWithWorker(AWorker(id: 1)), Tick(TimeSpan.FromSeconds(1))).Model;

        var changed = After(asked, Snap(Snapshot([], [], new RunSection(null, null), new WorkerSection(true, "hetzner", AWorker(id: 2)), [])));
        var late = After(changed, new WorkerCheckedMsg(0, new WorkerCheck(AWorker(id: 1, status: "running"), 0.0119m, null)));

        late.Worker.Check.Should().BeNull();
        Update(late, Tick(TimeSpan.FromSeconds(2))).Effects.Should().Equal(new RefreshWorkerCheck(1));
    }

    [Fact]
    public void AnAnswerIsKeptWhetherOrNotItReachedHetzner()
    {
        var check = new WorkerCheck(null, null, "Hetzner timed out after 15 seconds.");
        var model = After(ManagedWithWorker(), Tick(TimeSpan.FromSeconds(1)), new WorkerCheckedMsg(0, check));

        model.Worker.Check.Should().Be(check);
        model.Worker.CheckInFlight.Should().BeFalse();
    }

    [Fact]
    public void RInTheWorkerPaneAsksHetznerNowUnlessAnAnswerIsAlreadyOnItsWay()
    {
        var model = ManagedWithWorker(AWorker()) with { Focus = Pane.Worker };

        var asked = Update(model, Letter('r'));
        asked.Effects.Should().Equal(new RefreshWorkerCheck(0));
        asked.Model.Worker.CheckInFlight.Should().BeTrue();

        var again = Update(asked.Model, Letter('r'));
        again.Effects.Should().BeEmpty();
        again.Model.Notice.Should().NotBeNull();
    }

    [Fact]
    public void RInTheWorkerPaneSaysThereIsNothingToCheckWhenThereIsNoManagedWorker()
    {
        var model = After(Model(), Snap(Quiet([]))) with { Focus = Pane.Worker };

        var result = Update(model, Letter('r'));

        result.Effects.Should().BeEmpty();
        result.Model.Notice!.Text.Should().Contain("managed worker");
    }

    [Fact]
    public void ANoticeGoesAwayWhenItsTimeIsUp()
    {
        var model = After(Model(), Snap(Quiet([]))) with { Focus = Pane.Worker };
        model = After(model, Letter('r'));
        model.Notice.Should().NotBeNull();

        After(model, Tick(TimeSpan.FromSeconds(1))).Notice.Should().NotBeNull();
        After(model, Tick(TimeSpan.FromSeconds(10))).Notice.Should().BeNull();
    }

    [Fact]
    public void ATickCarriesTheClockAndTheWindowSize()
    {
        var model = After(Model(), Tick(TimeSpan.FromSeconds(5), width: 80, height: 30));

        (model.Now, model.Width, model.Height).Should().Be((Now.AddSeconds(5), 80, 30));
    }

    [Fact]
    public void AKeyThatMeansNothingInAPaneChangesNothing()
    {
        var model = After(Model(), Snap(Quiet([Queued(1)])));

        After(model, Letter('y')).Should().Be(model);
        After(model, Letter('z')).Should().Be(model);
    }
}
