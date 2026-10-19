using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli.Features.Tui;
using VideoOptimiser.Domain.Jobs;
using static VideoOptimiser.UnitTests.Features.Tui.TuiTestData;

namespace VideoOptimiser.UnitTests.Features.Tui;

/// <summary>
/// Each action key in each state it can meet: the dialog it opens, the effect it asks for, or the line in the footer that says
/// why it does nothing. Update is pure, so each test replays messages and reads the step.
/// </summary>
public sealed class TuiActionKeysTests
{
    private static readonly RemoteWorkerCreationOption Cx43 = new("cx43", "hel1", 0.0119m);
    private static readonly RemoteWorkerCreationOption Cx53 = new("cx53", "fsn1", 0.0237m);
    private static readonly LiveRunHolder Background = new(LockHolders.BackgroundRun, Now, 4242);

    /// <summary>The queue pane over these videos with the cursor on <paramref name="selected"/>.</summary>
    private static TuiModel Queue(int selected, params Video[] open) =>
        After(Model(), Snap(Quiet(open))) with { QueueSelection = Id(selected) };

    private static TuiModel WithWorker(TuiModel model, RemoteWorkerInfo? recorded = null, bool managed = true) =>
        After(model, Snap(Snapshot(worker: new WorkerSection(managed, managed ? "hetzner" : "manual", recorded))));

    private static TuiModel WithRun(TuiModel model, LiveRunHolder? holder, RunRequests? requests = null) =>
        After(model, Snap(Snapshot(run: new RunSection(holder, requests))));

    private static string? Notice(TuiStep step) => step.Model.Notice?.Text;

    private static TuiModel Focused(TuiModel model, Pane pane) => model with { Focus = pane };

    private static TuiStep Choose(TuiModel model, params TuiMsg[] keys)
    {
        var step = TuiStep.Of(model);
        foreach (var key in keys) step = Update(step.Model, key);
        return step;
    }

    private static TuiMsg Enter => Press(ConsoleKey.Enter, '\r');

    // ---- queue ------------------------------------------------------------------------------

    [Theory]
    [InlineData("Queue", null)]
    [InlineData("Held", "is already held")]
    [InlineData("Converting", "is converting now")]
    [InlineData("Ready", "Only a waiting or stopped video can be held")]
    [InlineData("Failed", "Only a waiting or stopped video can be held")]
    public void HAtOnceHoldsAWaitingVideoAndSaysWhyForAnyOther(string kind, string? notice)
    {
        var video = kind switch { "Queue" => Queued(1), "Held" => Held(1), "Converting" => Converting(1), "Ready" => Ready(1), _ => Failed(1) };
        var model = After(Model(), Snap(video.Group == VideoGroup.Failed ? Quiet([], [video]) : Quiet([video]))) with { Focus = Pane.Queue };

        var step = Update(model, Letter('h'));

        if (notice is null) step.Effects.Should().Equal(new QueueActionEffect(QueueAction.Hold, Id(1), "clip-001.mp4"));
        else
        {
            step.Effects.Should().BeEmpty();
            Notice(step).Should().Contain(notice);
        }
    }

    [Theory]
    [InlineData("Held", true)]
    [InlineData("Queue", false)]
    public void RAtOnceReleasesAHeldVideoAndSaysWhenNothingIsHeld(string kind, bool releases)
    {
        var model = Queue(1, kind == "Held" ? Held(1) : Queued(1));

        var step = Update(model, Letter('r'));

        if (releases) step.Effects.Should().Equal(new QueueActionEffect(QueueAction.Release, Id(1), "clip-001.mp4"));
        else Notice(step).Should().Be("clip-001.mp4 isn't held.");
    }

    [Theory]
    [InlineData("Queue", true)]
    [InlineData("Held", true)]
    [InlineData("Ready", false)]
    public void NAtOnceMovesAWaitingStoppedOrHeldVideoToTheFront(string kind, bool moves)
    {
        var video = kind switch { "Queue" => Queued(1), "Held" => Held(1), _ => Ready(1) };

        var step = Update(Queue(1, video), Letter('n'));

        if (moves) step.Effects.Should().Equal(new QueueActionEffect(QueueAction.Next, Id(1), "clip-001.mp4"));
        else Notice(step).Should().Be("Only a waiting, stopped or held video can run next.");
    }

    /// <summary>A drop can lose work, so it asks; what it says depends on what the video leaves behind.</summary>
    [Theory]
    [InlineData("Queue", "It leaves the queue. The original is untouched.")]
    [InlineData("Held", "It leaves the queue. The original is untouched.")]
    [InlineData("Ready", "The original is untouched. Its AV1 stays until cleanup deletes it.")]
    public void DAsksBeforeItDropsAndSaysWhatItLeaves(string kind, string detail)
    {
        var video = kind switch { "Queue" => Queued(1), "Held" => Held(1), _ => Ready(1) };

        var step = Update(Queue(1, video), Letter('d'));

        step.Effects.Should().BeEmpty();
        step.Model.Confirm.Should().Be(new TuiConfirm("Drop clip-001.mp4?", detail, new QueueActionEffect(QueueAction.Drop, Id(1), "clip-001.mp4")));
    }

    [Fact]
    public void DRefusesAVideoConvertingNow()
    {
        Notice(Update(WithRun(Queue(1, Converting(1)), Background), Letter('d'))).Should().Be("clip-001.mp4 is converting now. Press x to stop the run after this video, or X to stop it now.");
    }

    private const string DismissDetail = "Dismissed failures leave this list and stay in history. Their originals stay skipped; queue retry <job-id> still queues one again.";

    /// <summary>VOPT-115: a lone failure is dismissed after a yes, as a drop is.</summary>
    [Fact]
    public void DOnTheOnlyFailureAsksBeforeItDismissesIt()
    {
        var failed = After(Model(), Snap(Quiet([], [Failed(1)])));

        var step = Update(failed, Letter('d'));

        step.Effects.Should().BeEmpty();
        step.Model.Confirm.Should().Be(new TuiConfirm("Dismiss clip-001.mp4?", DismissDetail, new QueueActionEffect(QueueAction.Dismiss, Id(1), "clip-001.mp4")));
    }

    /// <summary>With more failures, the choice offers this one or all of them; Esc dismisses nothing.</summary>
    [Fact]
    public void DOnOneOfSeveralFailuresOffersThatOneOrAll()
    {
        var failed = After(Model(), Snap(Quiet([], [Failed(1), Failed(2)])));

        var choice = Update(failed, Letter('d')).Model.Choice!;

        (choice.Title, choice.Detail).Should().Be(("Dismiss failures", DismissDetail));
        choice.Options.Select(option => option.Label).Should().Equal("Dismiss clip-001.mp4", "Dismiss all 2 failures");
        Choose(failed, Letter('d'), Enter).Effects.Should().Equal(new QueueActionEffect(QueueAction.Dismiss, Id(1), "clip-001.mp4"));
        Choose(failed, Letter('d'), Press(ConsoleKey.DownArrow), Enter).Effects.Should().Equal(new DismissAllFailures());
        var cancelled = Choose(failed, Letter('d'), Press(ConsoleKey.Escape));
        cancelled.Model.Choice.Should().BeNull();
        cancelled.Effects.Should().BeEmpty();
    }

    /// <summary>The group shows at most <see cref="TuiSnapshotReader.MostFailed"/>, so at the cap it can't count them all.</summary>
    [Fact]
    public void AtTheFailedGroupsCapDismissAllNamesNoNumber()
    {
        var failed = After(Model(), Snap(Quiet([], [.. Enumerable.Range(1, TuiSnapshotReader.MostFailed).Select(number => Failed(number))])));

        Update(failed, Letter('d')).Model.Choice!.Options[1].Label.Should().Be("Dismiss every failure");
    }

    [Fact]
    public void TAsksBeforeItQueuesAFailedVideoAgainAndOnlyAFailedOne()
    {
        var failed = After(Model(), Snap(Quiet([], [Failed(7)])));

        Update(failed, Letter('t')).Model.Confirm.Should().Be(new TuiConfirm("Retry clip-007.mp4?", "A fresh job starts from scratch. The failed one stays in history.", new QueueActionEffect(QueueAction.Retry, Id(7), "clip-007.mp4")));
        Notice(Update(Queue(1, Queued(1)), Letter('t'))).Should().Be("Only a failed video can be retried.");
    }

    [Fact]
    public void AQueueKeyWithNothingSelectedSaysSo()
    {
        Notice(Update(After(Model(), Snap(Quiet([]))), Letter('h'))).Should().Be("Nothing is selected.");
    }

    /// <summary>The answer y runs the effect and holds its key; a second press on the same video is refused with what is being done.</summary>
    [Fact]
    public void ConfirmingStartsTheEffectOnceAndASecondOneOnTheSameVideoIsRefused()
    {
        var model = Queue(1, Queued(1));

        var first = Choose(model, Letter('d'), Letter('y'));

        first.Effects.Should().Equal(new QueueActionEffect(QueueAction.Drop, Id(1), "clip-001.mp4"));
        first.Model.Busy.Should().ContainKey($"queue:{Id(1):N}").WhoseValue.Should().Be("Dropping clip-001.mp4.");

        var second = Choose(first.Model, Letter('h'));
        second.Effects.Should().BeEmpty();
        Notice(second).Should().Be("Dropping clip-001.mp4.");

        // Another video is another thing.
        Choose(After(first.Model, Snap(Quiet([Queued(1), Queued(2)])), Press(ConsoleKey.DownArrow)), Letter('h')).Effects.Should().ContainSingle();
    }

    // ---- worker -----------------------------------------------------------------------------

    [Fact]
    public void CAsksHetznerForTheServersWhileTheWorkerPaneSaysSo()
    {
        var model = Focused(WithWorker(Model()), Pane.Worker);

        var step = Update(model, Letter('c'));

        step.Effects.Should().Equal(new LoadWorkerOptions(WorkerOptionsPurpose.Create));
        step.Model.Busy["worker"].Should().Be("Asking Hetzner for the servers.");
        // A second press while the list is coming is refused with the same words.
        Notice(Update(step.Model, Letter('c'))).Should().Be("Asking Hetzner for the servers.");
    }

    [Fact]
    public void CAndDSayWhyTheyDoNothingWhenThereIsNoManagedWorkerOrIsAlreadyOne()
    {
        var unmanaged = Focused(WithWorker(Model(), managed: false), Pane.Worker);
        Notice(Update(unmanaged, Letter('c'))).Should().Contain("no managed worker");
        Notice(Update(unmanaged, Letter('d'))).Should().Contain("no managed worker");

        var up = Focused(WithWorker(Model(), AWorker()), Pane.Worker);
        Notice(Update(up, Letter('c'))).Should().Be("The worker video-optimiser-1 is already up.");

        var none = Focused(WithWorker(Model()), Pane.Worker);
        Notice(Update(none, Letter('d'))).Should().Be("There is no worker to delete.");
    }

    [Fact]
    public void TheServersComeBackAsAListWithPricesTheFirstSelectedAndEnterCreatesTheChosenOne()
    {
        var model = Focused(WithWorker(Model()), Pane.Worker);
        model = Update(model, Letter('c')).Model;

        model = Update(model, new WorkerOptionsMsg(WorkerOptionsPurpose.Create, [Cx43, Cx53], null)).Model;

        model.Busy.Should().BeEmpty();
        model.Choice!.Selected.Should().Be(0);
        model.Choice.Options.Select(option => (option.Label, option.Hint)).Should().Equal(("cx43  hel1", "EUR 0.012/h"), ("cx53  fsn1", "EUR 0.024/h"));

        var chosen = Choose(model, Press(ConsoleKey.DownArrow), Press(ConsoleKey.DownArrow), Enter);
        chosen.Effects.Should().Equal(new CreateWorker(Cx53));
        chosen.Model.Choice.Should().BeNull();
        chosen.Model.Busy["worker"].Should().Be("Creating the worker.");
    }

    [Fact]
    public void EscClosesAListAndTouchesNothing()
    {
        var model = Update(Update(Focused(WithWorker(Model()), Pane.Worker), Letter('c')).Model, new WorkerOptionsMsg(WorkerOptionsPurpose.Create, [Cx43], null)).Model;

        var step = Choose(model, Press(ConsoleKey.Escape, '\u001b'));

        step.Model.Choice.Should().BeNull();
        step.Effects.Should().BeEmpty();
    }

    /// <summary>Hetzner's message lists what it tried per server type; each line is in the log, the first in the footer, and no list opens.</summary>
    [Fact]
    public void NoServerToCreateShowsHetznersReasonInTheLogAndTheFooter()
    {
        var model = Update(Focused(WithWorker(Model()), Pane.Worker), Letter('c')).Model;
        var reason = "No Hetzner worker could be created, so none is running. Tried:\n  cx43 in hel1: sold out\nTry again later, or add server types or locations.";

        var step = Update(model, new WorkerOptionsMsg(WorkerOptionsPurpose.Create, null, reason));

        step.Model.Choice.Should().BeNull();
        step.Model.Log.Select(entry => (entry.Kind, entry.Text)).Should().Equal(
            (LogKind.Error, "No Hetzner worker could be created, so none is running. Tried:"),
            (LogKind.Error, "cx43 in hel1: sold out"),
            (LogKind.Error, "Try again later, or add server types or locations."));
        Notice(step).Should().Be("No Hetzner worker could be created, so none is running. Tried:");
    }

    [Theory]
    [InlineData(false, "It stops billing at once.")]
    [InlineData(true, "It stops billing at once. The video converting now stops and starts again from its stage on the next worker.")]
    public void DAsksBeforeItDeletesTheWorkerAndWarnsWhenARunIsUsingIt(bool running, string detail)
    {
        var model = Focused(WithWorker(Model(), AWorker()), Pane.Worker);
        if (running) model = WithRun(model, Background);

        var step = Update(model, Letter('d'));

        step.Model.Confirm.Should().Be(new TuiConfirm("Delete the worker video-optimiser-1?", detail, new DeleteWorker()));
        Choose(step.Model, Letter('y')).Model.Busy["worker"].Should().Be("Deleting the worker.");
    }

    // ---- starting a run ---------------------------------------------------------------------

    private static TuiModel Startable(RemoteWorkerInfo? recorded = null, bool managed = true) =>
        WithWorker(After(Model(), Snap(Quiet([Queued(1)]))), recorded, managed);

    [Fact]
    public void SRefusesWhileARunIsGoingAndWhenNothingIsWaiting()
    {
        Notice(Update(WithRun(Startable(), Background), Letter('s'))).Should().Be("A run is going already.");
        Notice(Update(WithWorker(After(Model(), Snap(Quiet([Ready(1), Held(2)])))), Letter('s'))).Should().Be("No video is waiting to convert.");
        Notice(Update(Model() with { Worker = WorkerState.Unknown, Open = [Queued(1)] }, Letter('s'))).Should().Be("Still reading the worker.");
    }

    /// <summary>
    /// VOPT-154: while the worker pane deletes or creates the worker, s opens no dialog, since "Use X" could name a server
    /// that is about to be gone and the runner would then have no worker.
    /// </summary>
    [Theory]
    [InlineData(true, "The worker is being deleted. Start the run once it's gone.")]
    [InlineData(false, "The worker is being created. Start the run once it's up.")]
    public void SWhileTheWorkerIsBeingDeletedOrCreatedSaysSoAndOpensNoDialog(bool deleting, string notice)
    {
        var model = deleting
            ? Choose(Update(Focused(Startable(AWorker()), Pane.Worker), Letter('d')).Model, Letter('y')).Model
            : Choose(Update(Update(Focused(Startable(), Pane.Worker), Letter('c')).Model, new WorkerOptionsMsg(WorkerOptionsPurpose.Create, [Cx43], null)).Model, Enter).Model;

        var step = Update(model, Letter('s'));

        Notice(step).Should().Be(notice);
        step.Model.Choice.Should().BeNull();
        step.Effects.Should().BeEmpty();
    }

    [Fact]
    public void SWorksFromEveryPane()
    {
        foreach (var pane in Enum.GetValues<Pane>())
            Update(Focused(Startable(managed: false), pane), Letter('s')).Effects.Should().ContainSingle(effect => effect is StartRun, pane.ToString());
    }

    /// <summary>The config's mode stands when there is no managed worker to ask about, and no dialog opens.</summary>
    [Fact]
    public void SWithoutAManagedWorkerStartsAtOnceWithNoModeFlag()
    {
        var step = Update(Startable(managed: false), Letter('s'));

        step.Model.Choice.Should().BeNull();
        step.Effects.Should().Equal(new StartRun(new StartPlan(null, WorkerFateChoice.Delete, null)));
        step.Model.Busy["run"].Should().Be("Starting the run.");
    }

    [Fact]
    public void SWithAWorkerUpOffersItFirstAndNoWorkerLastAndNeverAsksHetzner()
    {
        var step = Update(Startable(AWorker()), Letter('s'));

        step.Effects.Should().BeEmpty();
        step.Model.Choice!.Options.Select(option => option.Label).Should().Equal("Use video-optimiser-1", "No worker: convert on this PC");
        step.Model.Choice.Selected.Should().Be(0);
    }

    [Fact]
    public void ChoosingTheWorkerThatIsUpAsksWhatHappensToItAndTheFirstAnswerIsDelete()
    {
        var model = Update(Startable(AWorker()), Letter('s')).Model;

        var fate = Choose(model, Enter);
        fate.Effects.Should().BeEmpty();
        fate.Model.Choice!.Title.Should().Be("When the run ends");
        fate.Model.Choice.Options.Select(option => option.Label).Should().Equal("Delete the worker", "Keep the worker");
        fate.Model.Choice.Selected.Should().Be(0);

        Choose(fate.Model, Enter).Effects.Should().Equal(new StartRun(new StartPlan(null, WorkerFateChoice.Delete, ProcessingModeOverride.Remote)));
        Choose(fate.Model, Press(ConsoleKey.DownArrow), Enter).Effects.Should().Equal(new StartRun(new StartPlan(null, WorkerFateChoice.Keep, ProcessingModeOverride.Remote)));
    }

    /// <summary>Converting on this PC has no worker to settle, so it starts at once with --local and the default fate.</summary>
    [Fact]
    public void ChoosingNoWorkerStartsOnThisPcWithoutAskingAboutTheFate()
    {
        var model = Update(Startable(AWorker()), Letter('s')).Model;

        var step = Choose(model, Press(ConsoleKey.DownArrow), Enter);

        step.Model.Choice.Should().BeNull();
        step.Effects.Should().Equal(new StartRun(new StartPlan(null, WorkerFateChoice.Delete, ProcessingModeOverride.Local)));
    }

    [Fact]
    public void SWithNoWorkerListsTheServersWithTheFirstSelectedThenAsksTheFateOfTheOneChosen()
    {
        var asked = Update(Startable(), Letter('s'));
        asked.Effects.Should().Equal(new LoadWorkerOptions(WorkerOptionsPurpose.Start));

        var listed = Update(asked.Model, new WorkerOptionsMsg(WorkerOptionsPurpose.Start, [Cx43, Cx53], null)).Model;
        listed.Choice!.Options.Select(option => option.Label).Should().Equal("cx43  hel1", "cx53  fsn1", "No worker: convert on this PC");
        listed.Choice.Options.Select(option => option.Hint).Take(2).Should().Equal("EUR 0.012/h", "EUR 0.024/h");
        listed.Choice.Selected.Should().Be(0);

        var fate = Choose(listed, Press(ConsoleKey.DownArrow), Enter);
        fate.Effects.Should().BeEmpty();
        Choose(fate.Model, Press(ConsoleKey.DownArrow), Enter).Effects.Should().Equal(new StartRun(new StartPlan(Cx53, WorkerFateChoice.Keep, ProcessingModeOverride.Remote)));
    }

    /// <summary>No token or no capacity must not strand the user: the reason shows, and the way out stays.</summary>
    [Fact]
    public void WhenTheServersCannotBeListedTheReasonShowsAndOnlyNoWorkerIsOffered()
    {
        var asked = Update(Startable(), Letter('s')).Model;

        var model = Update(asked, new WorkerOptionsMsg(WorkerOptionsPurpose.Start, null, "No Hetzner API token is set.")).Model;

        model.Choice!.Options.Select(option => option.Label).Should().Equal("No worker: convert on this PC");
        model.Choice.Detail.Should().Be("Couldn't list the servers: No Hetzner API token is set.");
        model.Log.Should().ContainSingle(entry => entry.Kind == LogKind.Warning && entry.Text == "Couldn't list the servers: No Hetzner API token is set.");
        Choose(model, Enter).Effects.Should().Equal(new StartRun(new StartPlan(null, WorkerFateChoice.Delete, ProcessingModeOverride.Local)));
    }

    // ---- stopping and the worker's fate -----------------------------------------------------

    private static TuiModel Running(RunRequests? requests = null, bool background = true, bool managed = true) =>
        WithRun(Focused(WithWorker(After(Model(), Snap(Quiet([Converting(1)]))), managed: managed), Pane.Activity), background ? Background : new LiveRunHolder(LockHolders.Run, Now, 99), requests);

    [Fact]
    public void XAsksTheRunToStopAfterThisVideoAtOnce()
    {
        var step = Update(Running(), Letter('x'));

        step.Effects.Should().Equal(new StopRun(Now: false));
        step.Model.Busy["stop"].Should().Be("Asking the run to stop after this video.");
    }

    [Fact]
    public void CapitalXAsksBeforeItStopsNowBecauseTheVideoStopsAndStartsAgainFromItsStage()
    {
        var step = Update(Running(), Letter('X'));

        step.Effects.Should().BeEmpty();
        step.Model.Confirm.Should().Be(new TuiConfirm("Stop the run now?", "The video converting now stops and starts again from its stage next time.", new StopRun(Now: true)));
    }

    [Theory]
    [InlineData('x')]
    [InlineData('X')]
    [InlineData('w')]
    public void StopAndFateKeysSayNoRunIsGoingWhenNoneIs(char key)
    {
        var model = Focused(WithWorker(Model()), Pane.Activity);

        Notice(Update(model, Letter(key))).Should().Be("No run is going.");
    }

    /// <summary>A foreground run never reads the requests row: only Ctrl+C in its window stops it, and it asks about its worker there.</summary>
    [Theory]
    [InlineData('x', "That run is in another window; only Ctrl+C there stops it.")]
    [InlineData('X', "That run is in another window; only Ctrl+C there stops it.")]
    [InlineData('w', "That run asks about its worker in its own window.")]
    public void StopAndFateKeysRefuseAForegroundRun(char key, string notice)
    {
        var step = Update(Running(background: false), Letter(key));

        Notice(step).Should().Be(notice);
        step.Effects.Should().BeEmpty();
    }

    [Fact]
    public void AStopAlreadyAskedIsNotAskedTwice()
    {
        Notice(Update(Running(new RunRequests(true, false, WorkerFateChoice.Delete)), Letter('x'))).Should().Be("A stop is already asked of the run.");
        Notice(Update(Running(new RunRequests(false, true, WorkerFateChoice.Delete)), Letter('X'))).Should().Be("The run is already stopping now.");
        // A stop after this video can still be made a stop now.
        Update(Running(new RunRequests(true, false, WorkerFateChoice.Delete)), Letter('X')).Model.Confirm.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Delete", 0)]
    [InlineData("Keep", 1)]
    public void WOffersTheFateWithTheCurrentOneSelectedAndEnterSetsTheChosenOne(string current, int selected)
    {
        var fate = Enum.Parse<WorkerFateChoice>(current);
        var model = Update(Running(new RunRequests(false, false, fate)), Letter('w')).Model;

        model.Choice!.Selected.Should().Be(selected);

        var other = selected == 0 ? WorkerFateChoice.Keep : WorkerFateChoice.Delete;
        var step = Choose(model, Press(selected == 0 ? ConsoleKey.DownArrow : ConsoleKey.UpArrow), Enter);
        step.Effects.Should().Equal(new SetWorkerFate(other));
    }

    [Fact]
    public void WSaysThereIsNoManagedWorkerWhenThereIsNone()
    {
        Notice(Update(Running(managed: false), Letter('w'))).Should().Be("There is no worker whose fate to set.");
    }

    /// <summary>Stopping is not a pane's business: like s, x and X work wherever the cursor is.</summary>
    [Theory]
    [InlineData("Queue")]
    [InlineData("Worker")]
    [InlineData("Activity")]
    [InlineData("Review")]
    public void XAndCapitalXWorkFromEveryPane(string pane)
    {
        var model = Focused(Running(), Enum.Parse<Pane>(pane));

        Update(model, Letter('x')).Effects.Should().Equal(new StopRun(Now: false));
        Update(model, Letter('X')).Model.Confirm.Should().Be(new TuiConfirm("Stop the run now?", "The video converting now stops and starts again from its stage next time.", new StopRun(Now: true)));
    }

    [Theory]
    [InlineData("Queue", 'x')]
    [InlineData("Worker", 'X')]
    [InlineData("Review", 'x')]
    public void StopKeysFromAnyPaneSayWhenNoRunIsGoingOrItIsInAnotherWindow(string pane, char key)
    {
        var none = Focused(After(Model(), Snap(Quiet([Ready(1)]))), Enum.Parse<Pane>(pane));
        Notice(Update(none, Letter(key))).Should().Be("No run is going.");

        var foreground = Focused(Running(background: false), Enum.Parse<Pane>(pane));
        Notice(Update(foreground, Letter(key))).Should().Be("That run is in another window; only Ctrl+C there stops it.");
    }

    /// <summary>A video converting cannot be held, run next or dropped, and the notice says how to stop the run that converts it.</summary>
    [Theory]
    [InlineData('h', "Background", "clip-001.mp4 is converting now. Press x to stop the run after this video, or X to stop it now.")]
    [InlineData('d', "Background", "clip-001.mp4 is converting now. Press x to stop the run after this video, or X to stop it now.")]
    [InlineData('n', "Background", "clip-001.mp4 is converting now. Press x to stop the run after this video, or X to stop it now.")]
    [InlineData('h', "Foreground", "clip-001.mp4 is converting now. Stop the run with Ctrl+C in its window.")]
    [InlineData('d', "Foreground", "clip-001.mp4 is converting now. Stop the run with Ctrl+C in its window.")]
    [InlineData('n', "Foreground", "clip-001.mp4 is converting now. Stop the run with Ctrl+C in its window.")]
    public void ConvertingNowNoticesNameTheWayToStopTheRun(char key, string kind, string expected)
    {
        var holder = kind == "Background" ? Background : new LiveRunHolder(LockHolders.ImmichRun, Now, 99);
        var model = WithRun(Queue(1, Converting(1)), holder);

        Notice(Update(model, Letter(key))).Should().Be(expected);
    }

    // ---- review -----------------------------------------------------------------------------

    private static TuiModel Reviewing(params Video[] open) => Focused(After(Model(), Snap(Quiet(open))), Pane.Review);

    [Fact]
    public void OAndAOpenTheSelectedVideosOriginalAndAv1AtOnce()
    {
        var model = Reviewing(Ready(1), Ready(2));
        model = After(model, Press(ConsoleKey.DownArrow));

        Update(model, Letter('o')).Effects.Should().Equal(new OpenFile(Id(2), Original: true, "clip-002.mp4"));
        Update(model, Letter('a')).Effects.Should().Equal(new OpenFile(Id(2), Original: false, "clip-002.mp4"));
        Update(model, Letter('o')).Model.Confirm.Should().BeNull();
    }

    [Fact]
    public void TheFinaliseKeysPrepareTheConfirmationForTheSelectedVideoOrForAll()
    {
        var model = After(Reviewing(Ready(1), Ready(2)), Press(ConsoleKey.DownArrow));

        Update(model, Letter('f')).Effects.Should().Equal(new PrepareFinalise(Id(2)));
        Update(model, Letter('F')).Effects.Should().Equal(new PrepareFinalise(null));
    }

    [Theory]
    [InlineData('o')]
    [InlineData('a')]
    [InlineData('f')]
    [InlineData('F')]
    [InlineData('r')]
    public void TheReviewKeysSayNothingIsReadyWhenNothingIs(char key)
    {
        var step = Update(Reviewing(Queued(1)), Letter(key));

        step.Effects.Should().BeEmpty();
        Notice(step).Should().Be("Nothing is ready to finalise.");
    }

    /// <summary>x is a global stop key and opens nothing in this pane; the pane's own reject key is r.</summary>
    [Fact]
    public void XInTheReviewPaneDoesNothing()
    {
        var step = Update(Reviewing(Ready(1)), Letter('x'));

        step.Effects.Should().BeEmpty();
        step.Model.Confirm.Should().BeNull();
    }

    /// <summary>A reject deletes the AV1 for good, so it asks first, says what goes and what stays, and names the selected video.</summary>
    [Theory]
    [InlineData(false, "Its AV1 is deleted at once. The original is untouched.")]
    [InlineData(true, "Its AV1 and downloaded copy are deleted at once. The original stays in Immich.")]
    public void RAsksBeforeItRejectsTheSelectedVideoAndSaysWhatItDeletes(bool immich, string detail)
    {
        var model = After(Reviewing(Ready(1), Ready(2) with { Immich = immich }), Press(ConsoleKey.DownArrow));

        var step = Update(model, Letter('r'));

        step.Effects.Should().BeEmpty();
        step.Model.Confirm.Should().Be(new TuiConfirm("Reject clip-002.mp4?", detail, new QueueActionEffect(QueueAction.Reject, Id(2), "clip-002.mp4")));
    }

    [Fact]
    public void YStartsTheRejectHoldingTheVideosKeyAndANoOrAnyOtherKeyCancelsIt()
    {
        var asked = Update(Reviewing(Ready(1)), Letter('r')).Model;

        var cancelled = Update(asked, Letter('n'));
        cancelled.Effects.Should().BeEmpty();
        cancelled.Model.Confirm.Should().BeNull();

        var yes = Update(asked, Letter('y'));
        yes.Effects.Should().Equal(new QueueActionEffect(QueueAction.Reject, Id(1), "clip-001.mp4"));
        yes.Model.Busy[$"queue:{Id(1):N}"].Should().Be("Rejecting clip-001.mp4.");
        // Asking again is allowed, but the answer is refused while the first is out.
        var again = Choose(yes.Model, Letter('r'), Letter('y'));
        again.Effects.Should().BeEmpty();
        Notice(again).Should().Be("Rejecting clip-001.mp4.");
    }

    /// <summary>A video in the pane that is finalising, or stopped while it was, is finalize's; the key says so instead of asking.</summary>
    [Theory]
    [InlineData(JobStatus.Finalizing)]
    [InlineData(JobStatus.Interrupted)]
    public void RDoesNotAskAboutAVideoPartWayThroughFinalising(JobStatus status)
    {
        var step = Update(Reviewing(Ready(1) with { Status = status }), Letter('r'));

        step.Effects.Should().BeEmpty();
        step.Model.Confirm.Should().BeNull();
        Notice(step).Should().Be("clip-001.mp4 is part way through finalising, so it can't be rejected.");
    }

    [Fact]
    public void ThePreparedConfirmationOpensAndYStartsFinalisingHoldingItsKey()
    {
        var confirm = new TuiConfirm("Finalise 2 videos?", "details", new FinaliseVideos([Id(1), Id(2)]));
        var asked = Update(Reviewing(Ready(1), Ready(2)), Letter('F'));
        asked.Model.Busy["finalise"].Should().Be("Reading what is ready to finalise.");
        // With no batch out yet, the preparation's key still refuses a second press.
        Notice(Update(asked.Model, Letter('F'))).Should().Be("Reading what is ready to finalise.");

        var prepared = Update(asked.Model, new PreparedMsg(new PrepareFinalise(null), confirm, null));
        prepared.Model.Confirm.Should().Be(confirm);
        prepared.Model.Busy.Should().BeEmpty();

        var yes = Update(prepared.Model, Letter('y'));
        yes.Effects.Should().ContainSingle().Which.Should().BeOfType<FinaliseVideos>().Which.Ids.Should().Equal(Id(1), Id(2));
        yes.Model.Busy["finalise"].Should().Be("Finalising.");
        yes.Model.Finalising.Should().BeEquivalentTo(new FinaliseBatch([Id(1), Id(2)], null, 0, 0, 0), options => options.ComparingByMembers<FinaliseBatch>());
    }

    /// <summary>
    /// VOPT-123: while a batch is out, f, F and r answer at once and ask nothing. A row of the batch says where it is in it;
    /// any other row says the key waits for the batch, r on every row since rejecting needs the lock the batch holds.
    /// </summary>
    [Theory]
    [InlineData('f', 1, "clip-001.mp4 is being finalised now.")]
    [InlineData('F', 1, "clip-001.mp4 is being finalised now.")]
    [InlineData('r', 1, "clip-001.mp4 is being finalised now.")]
    [InlineData('f', 2, "clip-002.mp4 is queued to be finalised.")]
    [InlineData('F', 2, "clip-002.mp4 is queued to be finalised.")]
    [InlineData('r', 2, "clip-002.mp4 is queued to be finalised.")]
    [InlineData('f', 3, "A finalise is going. You can start another once it ends.")]
    [InlineData('F', 3, "A finalise is going. You can start another once it ends.")]
    [InlineData('r', 3, "A finalise is going. You can reject once it ends.")]
    public void DuringABatchTheFinaliseAndRejectKeysAnswerAtOnce(char key, int selected, string notice)
    {
        // clip-001 is being finalised, clip-002 waits for it, clip-003 is outside the batch.
        var model = Batch(selected);

        var step = Update(model, Letter(key));

        step.Effects.Should().BeEmpty();
        (step.Model.Confirm, step.Model.Choice).Should().Be((null, null));
        step.Model.Busy.Should().BeSameAs(model.Busy);
        step.Model.Finalising.Should().BeSameAs(model.Finalising);
        Notice(step).Should().Be(notice);
        if (selected == 3) notice.Should().NotContain("clip-003.mp4");
    }

    /// <summary>The batch is progress, not a lock: a queued video can still be watched before its turn.</summary>
    [Fact]
    public void DuringABatchOAndAStillOpenAQueuedVideo()
    {
        var model = Batch(selected: 2);

        Update(model, Letter('o')).Effects.Should().Equal(new OpenFile(Id(2), Original: true, "clip-002.mp4"));
        Update(model, Letter('a')).Effects.Should().Equal(new OpenFile(Id(2), Original: false, "clip-002.mp4"));
    }

    /// <summary>
    /// VOPT-124: Esc stops a batch after the video it is on, from any pane, without asking. A second press, or a batch with only
    /// that video left, says why nothing more happens; with no batch out Esc does nothing.
    /// </summary>
    [Theory]
    [InlineData("no batch", false, null)]
    [InlineData("two left", true, "The finalise stops after this video.")]
    [InlineData("stop asked", false, "A stop is already asked of the finalise.")]
    [InlineData("one left", false, "Only the video being finalised is left, and it always finishes.")]
    public void EscStopsAFinaliseBatchAfterTheVideoItIsOn(string state, bool stops, string? notice)
    {
        var model = state switch
        {
            "no batch" => Reviewing(Ready(1), Ready(2)),
            "two left" => Batch(selected: 1),
            "stop asked" => Update(Batch(selected: 1), Press(ConsoleKey.Escape, '\u001b')).Model,
            _ => After(Batch(selected: 1), new FinaliseEndedMsg(Id(1), true, 0, new LogLine(LogKind.Success, "Finalised clip-001.mp4.")), new FinaliseStartedMsg(Id(2)))
        } with { Focus = Pane.Activity, Notice = null };

        var step = Update(model, Press(ConsoleKey.Escape, '\u001b'));

        Notice(step).Should().Be(notice);
        (step.Model.Confirm, step.Model.Choice).Should().Be((null, null));
        if (!stops)
        {
            step.Effects.Should().BeEmpty();
            step.Model.Finalising.Should().Be(model.Finalising);
            step.Model.Busy.Should().BeSameAs(model.Busy);
            return;
        }
        step.Effects.Should().Equal(new StopFinalise());
        step.Model.Finalising!.StopAsked.Should().BeTrue();
        step.Model.Busy["finalise"].Should().Be("Stopping the finalise after this video.");
    }

    /// <summary>Quitting mid-batch cuts a video off; while Esc would stop the batch cleanly, the quit dialog says so.</summary>
    [Fact]
    public void TheQuitDialogNamesEscWhileItWouldStopTheBatch()
    {
        var batch = Batch(selected: 1);

        Update(batch, Letter('q')).Model.Confirm!.Detail.Should().EndWith(TuiUpdate.EscStopsTheFinalise);
        var stopping = Update(batch, Press(ConsoleKey.Escape, '\u001b')).Model;
        Update(stopping, Letter('q')).Model.Confirm!.Detail.Should().NotContain("Esc");
    }

    /// <summary>Three ready videos with a finalise of the first two out, on clip-001, the cursor on <paramref name="selected"/>.</summary>
    private static TuiModel Batch(int selected)
    {
        var confirm = new TuiConfirm("Finalise 2 videos?", "details", new FinaliseVideos([Id(1), Id(2)]));
        var model = Update(Reviewing(Ready(1), Ready(2), Ready(3)) with { Dialog = confirm }, Letter('y')).Model;
        return After(model, new FinaliseStartedMsg(Id(1))) with { ReviewSelection = Id(selected) };
    }

    /// <summary>The batch's progress moves Review's count and its current row; each ended video's line goes in the log as it ends.</summary>
    [Fact]
    public void AFinalisesProgressMovesTheCountTheCurrentRowAndTheSavedTotal()
    {
        var model = Update(Reviewing(Ready(1), Ready(2), Ready(3)) with { Dialog = new TuiConfirm("Finalise 3 videos?", "details", new FinaliseVideos([Id(1), Id(2), Id(3)])) }, Letter('y')).Model;

        model = After(model, new FinaliseStartedMsg(Id(1)));
        model.Finalising!.Current.Should().Be(Id(1));
        (model.Finalising.Position, model.Finalising.IsWaiting(Id(1)), model.Finalising.IsWaiting(Id(2))).Should().Be((1, false, true));

        model = After(model,
            new FinaliseEndedMsg(Id(1), true, 600, new LogLine(LogKind.Success, "Finalised clip-001.mp4.")),
            new FinaliseStartedMsg(Id(2)),
            new FinaliseEndedMsg(Id(2), false, 0, new LogLine(LogKind.Error, "Couldn't finalise clip-002.mp4.")),
            new FinaliseStartedMsg(Id(3)));

        model.Finalising.Should().BeEquivalentTo(new FinaliseBatch([Id(1), Id(2), Id(3)], Id(3), 2, 1, 600), options => options.ComparingByMembers<FinaliseBatch>());
        model.Finalising!.Position.Should().Be(3);
        model.Log.Select(entry => entry.Text).Should().Equal("Finalised clip-001.mp4.", "Couldn't finalise clip-002.mp4.");
    }

    /// <summary>The batch's end clears it; progress with no batch out has nothing to move and is dropped.</summary>
    [Fact]
    public void TheBatchClearsWhenTheFinaliseEnds()
    {
        var model = Update(Reviewing(Ready(1), Ready(2)) with { Dialog = new TuiConfirm("Finalise 2 videos?", "details", new FinaliseVideos([Id(1), Id(2)])) }, Letter('y')).Model;

        model = After(model, new FinaliseStartedMsg(Id(1)), new ActionDoneMsg("finalise", new ActionResult([], "Finalised 2 videos.")));

        model.Finalising.Should().BeNull();
        After(model, new FinaliseStartedMsg(Id(2))).Should().Be(model);
    }

    [Fact]
    public void APreparationThatFoundNothingSaysSoInTheFooter()
    {
        var model = Update(Reviewing(Ready(1)), Letter('F')).Model;

        var step = Update(model, new PreparedMsg(new PrepareFinalise(null), null, "Nothing is ready to finalise."));

        step.Model.Confirm.Should().BeNull();
        step.Model.Busy.Should().BeEmpty();
        Notice(step).Should().Be("Nothing is ready to finalise.");
    }

    /// <summary>F with only warned videos names them, which the footer's one line can't hold, so the names go in the log.</summary>
    [Fact]
    public void APreparationsLogLinesGoInTheLogBesideItsFooterNotice()
    {
        var model = Update(Reviewing(Ready(1) with { Warnings = ["The AV1 has no audio."] }), Letter('F')).Model;
        LogLine[] lines = [new(LogKind.Warning, "Nothing to finalise in bulk."), new(LogKind.Detail, "clip-001.mp4")];

        var step = Update(model, new PreparedMsg(new PrepareFinalise(null), null, "Nothing to finalise in bulk.", lines));

        step.Model.Log.Select(entry => (entry.Kind, entry.Text)).Should().EndWith([(LogKind.Warning, "Nothing to finalise in bulk."), (LogKind.Detail, "clip-001.mp4")]);
        Notice(step).Should().Be("Nothing to finalise in bulk.");
    }

    // ---- what the effects report ------------------------------------------------------------

    [Fact]
    public void AnActionsResultFreesItsKeyLogsItsLinesSaysItsNoticeAndRefreshesTheSnapshot()
    {
        var model = Update(Queue(1, Queued(1)), Letter('h')).Model;

        var step = Update(model, new ActionDoneMsg($"queue:{Id(1):N}", new ActionResult([new LogLine(LogKind.Action, "Held clip-001.mp4.")], "Held clip-001.mp4.")));

        step.Model.Busy.Should().BeEmpty();
        step.Model.Log.Should().ContainSingle().Which.Should().Be(new LogEntry(Now, LogKind.Action, "Held clip-001.mp4."));
        Notice(step).Should().Be("Held clip-001.mp4.");
        step.Effects.Should().Equal(new RefreshSnapshot());
    }

    /// <summary>What Hetzner said about the worker is stale once an action touched it: it is forgotten, an answer still on its way is dropped, and the next tick asks again.</summary>
    [Fact]
    public void AnActionOnTheWorkerForgetsWhatHetznerSaidAndAsksAgainAtTheNextTick()
    {
        var model = Update(WithWorker(Model(), AWorker()), Tick(TimeSpan.FromSeconds(1))).Model;
        model = After(model, new WorkerCheckedMsg(0, new WorkerCheck(AWorker(status: "running"), 0.0119m, null)));
        model.Worker.Check.Should().NotBeNull();

        model = Update(model, new ActionDoneMsg("worker", new ActionResult([], "Deleted the worker.", RefreshWorker: true))).Model;

        model.Worker.Check.Should().BeNull();
        Update(model, Tick(TimeSpan.FromSeconds(2))).Effects.Should().Equal(new RefreshWorkerCheck(1));
    }

    /// <summary>
    /// <c>r</c> in the worker pane says it is asking, and the answer replaces that line: with no worker the pane looks the same
    /// before and after, so without it the press seems to do nothing. Hetzner can take longer than a notice lasts, so the answer
    /// still shows after one. A check the clock asked for says nothing.
    /// </summary>
    [Theory]
    [InlineData(null, "Checked with Hetzner just now.")]
    [InlineData("Hetzner timed out after 15 seconds.", "Couldn't ask Hetzner: Hetzner timed out after 15 seconds.")]
    public void CheckingTheWorkerByHandSaysWhenHetznerAnswered(string? notChecked, string said)
    {
        var asked = Update(Focused(WithWorker(Model()), Pane.Worker), Letter('r'));
        Notice(asked).Should().Be("Asking Hetzner.");
        var generation = asked.Effects.OfType<RefreshWorkerCheck>().Single().Generation;
        var waiting = Update(asked.Model, Tick(TimeSpan.FromSeconds(10)));
        Notice(waiting).Should().Be("Asking Hetzner.");

        var answered = Update(waiting.Model, new WorkerCheckedMsg(generation, new WorkerCheck(null, null, notChecked)));

        Notice(answered).Should().Be(said);
        var byTheClock = Update(WithWorker(Model()), Tick(TimeSpan.FromSeconds(1))).Model;
        After(byTheClock, new WorkerCheckedMsg(byTheClock.Worker.Generation, new WorkerCheck(null, null, null))).Notice.Should().BeNull();
    }

    /// <summary>A press while the clock's question is out waits for that answer instead of asking twice, and still reports it.</summary>
    [Fact]
    public void CheckingTheWorkerWhileTheClockIsAskingReportsThatAnswer()
    {
        var byTheClock = Update(Focused(WithWorker(Model()), Pane.Worker), Tick(TimeSpan.FromSeconds(1)));
        var generation = byTheClock.Effects.OfType<RefreshWorkerCheck>().Single().Generation;

        var pressed = Update(byTheClock.Model, Letter('r'));
        pressed.Effects.Should().BeEmpty();
        Notice(pressed).Should().Be("Asking Hetzner.");

        Notice(Update(pressed.Model, new WorkerCheckedMsg(generation, new WorkerCheck(null, null, null)))).Should().Be("Checked with Hetzner just now.");
    }

    [Fact]
    public void ALineAnEffectReportsWhileItWorksGoesInTheLog()
    {
        var model = After(Model(), new TuiLogMsg(LogKind.Info, "Creating Hetzner cx43 in hel1."));

        model.Log.Should().ContainSingle().Which.Should().Be(new LogEntry(Now, LogKind.Info, "Creating Hetzner cx43 in hel1."));
    }

    // ---- quitting while an action is going --------------------------------------------------

    private static readonly TuiMsg CtrlC = Press(ConsoleKey.C, '\u0003', ConsoleModifiers.Control);

    /// <summary>The worker is being deleted: an action in flight, which quitting would cut off partway.</summary>
    private static TuiModel Deleting() =>
        Choose(Focused(WithWorker(Model(), AWorker()), Pane.Worker), Letter('d'), Letter('y')).Model;

    [Fact]
    public void QuitsAtOnceWhenNothingIsInFlightOrOnlyAReadIs()
    {
        Update(Model(), Letter('q')).Model.Quit.Should().BeTrue();
        Update(Model(), CtrlC).Model.Quit.Should().BeTrue();

        // Hetzner's check and the snapshot are reads: nothing to cut off.
        var asking = Update(WithWorker(Model(), AWorker()), Tick(TimeSpan.FromSeconds(1))).Model;
        asking.Worker.CheckInFlight.Should().BeTrue();
        Update(asking, Letter('q')).Model.Quit.Should().BeTrue();
        Update(asking, CtrlC).Model.Quit.Should().BeTrue();
    }

    /// <summary>Quitting ends the process, so a create between Hetzner's request and the state file, or a finalise mid-install, would stop partway.</summary>
    [Theory]
    [InlineData("q")]
    [InlineData("ctrl+c")]
    public void QuitWhileAnActionIsInFlightAsksFirstAndNamesWhatIsStillGoing(string key)
    {
        var step = Update(Deleting(), key == "q" ? Letter('q') : CtrlC);

        step.Model.Quit.Should().BeFalse();
        step.Effects.Should().BeEmpty();
        step.Model.Confirm.Should().Be(new TuiConfirm("Deleting the worker is still going.", "Quitting now stops it partway. It's better to wait for it to finish.", new QuitNow()));
    }

    [Fact]
    public void YQuitsAnyway()
    {
        var asked = Update(Deleting(), Letter('q')).Model;

        Update(asked, Letter('y')).Model.Quit.Should().BeTrue();
    }

    [Theory]
    [InlineData('n')]
    [InlineData('q')]
    [InlineData('\r')]
    public void AnyOtherKeyKeepsTheScreenOpenAndTheActionRunning(char key)
    {
        var asked = Update(Deleting(), Letter('q')).Model;

        var step = Update(asked, key == '\r' ? Enter : Letter(key));

        step.Model.Quit.Should().BeFalse();
        step.Model.Confirm.Should().BeNull();
        step.Model.Busy.Should().ContainKey("worker");
        step.Effects.Should().BeEmpty();
    }

    /// <summary>Ctrl+C twice always gets out, whatever is going.</summary>
    [Fact]
    public void ASecondCtrlCInsideTheDialogQuits()
    {
        var asked = Update(Deleting(), CtrlC).Model;

        Update(asked, CtrlC).Model.Quit.Should().BeTrue();
    }

    [Fact]
    public void SeveralActionsInFlightAreCountedAndListed()
    {
        var model = Deleting();
        model = Update(After(model, Snap(Quiet([Queued(1)]))) with { Focus = Pane.Queue }, Letter('h')).Model;

        var confirm = Update(model, Letter('q')).Model.Confirm!;

        confirm.Question.Should().Be("2 actions are still going.");
        confirm.Detail.Should().Be("Deleting the worker; Holding clip-001.mp4. Quitting now stops them partway. It's better to wait for them to finish.");
    }

    // ---- the key guide ----------------------------------------------------------------------

    private static readonly TuiMsg Question = Press(ConsoleKey.Oem2, '?', ConsoleModifiers.Shift);

    [Theory]
    [InlineData("Queue")]
    [InlineData("Worker")]
    [InlineData("Activity")]
    [InlineData("Review")]
    public void QuestionMarkOpensTheGuideFromEveryPane(string pane)
    {
        var step = Update(Focused(After(Model(), Snap(Quiet([Queued(1)]))), Enum.Parse<Pane>(pane)), Question);

        step.Model.Help.Should().BeTrue();
        step.Effects.Should().BeEmpty();
    }

    /// <summary>The key that closes the guide does nothing else: h would hold the video, q would quit, tab would move.</summary>
    [Theory]
    [InlineData('h')]
    [InlineData('q')]
    [InlineData('s')]
    [InlineData('x')]
    [InlineData('?')]
    [InlineData('\t')]
    public void AnyKeyClosesTheGuideWithoutActing(char key)
    {
        var open = Update(Focused(After(Model(), Snap(Quiet([Queued(1)]))), Pane.Queue), Question).Model;

        var step = Update(open, key == '\t' ? Press(ConsoleKey.Tab, '\t') : Letter(key));

        step.Model.Help.Should().BeFalse();
        step.Model.Quit.Should().BeFalse();
        step.Model.Focus.Should().Be(Pane.Queue);
        step.Model.Busy.Should().BeEmpty();
        step.Effects.Should().BeEmpty();
        step.Model.Notice.Should().BeNull();
    }

    /// <summary>A question being answered stays open: the guide is for the screen behind it.</summary>
    [Fact]
    public void QuestionMarkIsIgnoredWhileADialogIsOpen()
    {
        var confirming = Update(Queue(1, Queued(1)), Letter('d')).Model;
        var confirmStep = Update(confirming, Question);
        confirmStep.Model.Help.Should().BeFalse();
        confirmStep.Model.Confirm.Should().NotBeNull();

        var choosing = Update(Startable(AWorker()), Letter('s')).Model;
        var choiceStep = Update(choosing, Question);
        choiceStep.Model.Help.Should().BeFalse();
        choiceStep.Model.Choice.Should().NotBeNull();
    }

    // ---- dialogs ----------------------------------------------------------------------------

    [Fact]
    public void AListIgnoresOtherKeysAndStopsAtItsEnds()
    {
        var model = Update(Startable(AWorker()), Letter('s')).Model;

        Choose(model, Letter('q')).Model.Quit.Should().BeFalse();
        Choose(model, Press(ConsoleKey.UpArrow)).Model.Choice!.Selected.Should().Be(0);
        Choose(model, Press(ConsoleKey.DownArrow), Press(ConsoleKey.DownArrow), Press(ConsoleKey.DownArrow)).Model.Choice!.Selected.Should().Be(1);
        // Ctrl+C still quits.
        Choose(model, Press(ConsoleKey.C, '\u0003', ConsoleModifiers.Control)).Model.Quit.Should().BeTrue();
    }

    // ---- an effect's answer over an open dialog (VOPT-141) ----------------------------------

    /// <summary>
    /// The servers can take seconds to list, and the user may open a reject meanwhile. The list must not cover it: the reject
    /// would still take the keys, so y would delete an AV1 behind a list the user was reading.
    /// </summary>
    [Theory]
    [InlineData('s')]
    [InlineData('c')]
    public void AServerListThatComesInOverAnOpenQuestionIsDroppedAndNamesTheKeyThatAsksAgain(char key)
    {
        var purpose = key == 's' ? WorkerOptionsPurpose.Start : WorkerOptionsPurpose.Create;
        var model = Focused(WithWorker(After(Model(), Snap(Quiet([Queued(1), Ready(2)])))), purpose == WorkerOptionsPurpose.Start ? Pane.Queue : Pane.Worker);
        var asked = Update(model, Letter(key));
        asked.Effects.Should().Equal(new LoadWorkerOptions(purpose));
        var rejecting = Update(Focused(asked.Model, Pane.Review), Letter('r')).Model;
        var reject = rejecting.Confirm;

        var step = Update(rejecting, new WorkerOptionsMsg(purpose, [Cx43], null));

        step.Model.Dialog.Should().Be(reject);
        step.Model.Busy.Should().BeEmpty();
        Notice(step).Should().Be($"The server list came in while a question was open. Press {key} again.");
        // The question on screen is the one y answers.
        Update(step.Model, Letter('y')).Effects.Should().Equal(new QueueActionEffect(QueueAction.Reject, Id(2), "clip-002.mp4"));
    }

    /// <summary>A finalise confirmation that comes in over another question must not replace it, or a y meant for a drop finalises.</summary>
    [Theory]
    [InlineData('f')]
    [InlineData('F')]
    public void AFinaliseConfirmationThatComesInOverAnOpenQuestionIsDroppedAndNamesTheKeyThatAsksAgain(char key)
    {
        var asked = Update(Reviewing(Queued(1), Ready(2)), Letter(key));
        var prepare = asked.Effects.Should().ContainSingle().Which.Should().BeOfType<PrepareFinalise>().Subject;
        var dropping = Update(Focused(asked.Model, Pane.Queue), Letter('d')).Model;
        var drop = dropping.Confirm;

        var step = Update(dropping, new PreparedMsg(prepare, new TuiConfirm("Finalise clip-002.mp4?", "details", new FinaliseVideos([Id(2)])), null));

        step.Model.Dialog.Should().Be(drop);
        step.Model.Busy.Should().BeEmpty();
        Notice(step).Should().Be($"The finalise question came in while another was open. Press {key} again.");
        Update(step.Model, Letter('y')).Effects.Should().Equal(new QueueActionEffect(QueueAction.Drop, Id(1), "clip-001.mp4"));
    }

    [Fact]
    public void TheQuitQuestionStaysWhenAnEffectsAnswerComesIn()
    {
        var busy = Update(Update(WithWorker(Reviewing(Queued(1), Ready(2))), Letter('s')).Model, Letter('f')).Model;
        var quitting = Update(busy, Letter('q')).Model;
        quitting.Confirm!.OnYes.Should().Be(new QuitNow());

        var listed = Update(quitting, new WorkerOptionsMsg(WorkerOptionsPurpose.Start, [Cx43], null)).Model;
        var prepared = Update(listed, new PreparedMsg(new PrepareFinalise(Id(2)), new TuiConfirm("Finalise clip-002.mp4?", "details", new FinaliseVideos([Id(2)])), null)).Model;

        prepared.Dialog.Should().Be(quitting.Dialog);
        Update(prepared, Letter('y')).Model.Quit.Should().BeTrue();
    }

    /// <summary>The key guide is no question: any key closes it, so an answer opens under it and shows once it closes.</summary>
    [Fact]
    public void AnEffectsAnswerOpensUnderTheKeyGuide()
    {
        var asked = Update(Startable(), Letter('s')).Model;
        var guide = Update(asked, Question).Model;

        var listed = Update(guide, new WorkerOptionsMsg(WorkerOptionsPurpose.Start, [Cx43], null)).Model;

        listed.Help.Should().BeTrue();
        Update(listed, Letter('x')).Model.Choice!.Title.Should().Be("Which worker converts?");
    }

    // ---- the cost page ----------------------------------------------------------------------

    private static TuiModel CostOpen(Pane pane = Pane.Queue) =>
        After(Focused(After(Model(), Snap(Quiet([Queued(1)]))), pane), Dollar, new CostReadMsg(ACostReport(), null));

    [Theory]
    [InlineData("Queue")]
    [InlineData("Worker")]
    [InlineData("Activity")]
    [InlineData("Review")]
    public void DollarOpensTheCostPageFromEveryPaneAndReadsTheReport(string pane)
    {
        var step = Update(Focused(After(Model(), Snap(Quiet([Queued(1)]))), Enum.Parse<Pane>(pane)), Dollar);

        step.Model.Cost.Should().Be(new CostPage(null, null, 0, Reading: true, AskedAt: Now, Problem: null));
        step.Effects.Should().Equal(new ReadCost());
    }

    /// <summary>A dialog takes the key first: <c>$</c> answers a question as any key but y does, and moves nothing in a list.</summary>
    [Fact]
    public void DollarDoesNotOpenThePageWhileADialogIsOpen()
    {
        var confirmStep = Update(Update(Queue(1, Queued(1)), Letter('d')).Model, Dollar);
        (confirmStep.Model.Cost, confirmStep.Model.Confirm).Should().Be((null, null));
        confirmStep.Effects.Should().BeEmpty();

        var choiceStep = Update(Update(Startable(AWorker()), Letter('s')).Model, Dollar);
        choiceStep.Model.Cost.Should().BeNull();
        choiceStep.Model.Choice.Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DollarOrEscClosesThePage(bool escape)
    {
        var step = Update(CostOpen(), escape ? Press(ConsoleKey.Escape, '') : Dollar);

        step.Model.Cost.Should().BeNull();
        step.Effects.Should().BeEmpty();
    }

    /// <summary>The page takes the keys: h would hold the video, q would quit, s would start a run, tab would move.</summary>
    [Theory]
    [InlineData('h')]
    [InlineData('q')]
    [InlineData('s')]
    [InlineData('x')]
    [InlineData('?')]
    [InlineData('	')]
    public void KeysOtherThanNavigationDoNothingOnThePage(char key)
    {
        var open = CostOpen();

        var step = Update(open, key == '	' ? Press(ConsoleKey.Tab, '	') : Letter(key));

        step.Model.Should().Be(open);
        step.Effects.Should().BeEmpty();
    }

    [Fact]
    public void CtrlCStillQuitsFromThePage() =>
        Update(CostOpen(), Press(ConsoleKey.C, '', ConsoleModifiers.Control)).Model.Quit.Should().BeTrue();

    /// <summary>Arrows select a run and stop at the ends; the page keys scroll that run's videos, and another run starts at its top.</summary>
    [Fact]
    public void ArrowsSelectARunAndPageKeysScrollItsVideos()
    {
        var open = CostOpen();
        open.Cost!.SelectedRun!.Id.Should().Be(Id(2));

        var scrolled = After(open, Press(ConsoleKey.PageDown));
        scrolled.Cost!.VideoScroll.Should().Be(3);
        After(scrolled, Press(ConsoleKey.PageUp)).Cost!.VideoScroll.Should().Be(0);

        var older = After(scrolled, Press(ConsoleKey.DownArrow), Press(ConsoleKey.DownArrow));
        (older.Cost!.SelectedRun!.Id, older.Cost.VideoScroll).Should().Be((Id(1), 0));
        After(older, Press(ConsoleKey.Home)).Cost!.SelectedRun!.Id.Should().Be(Id(2));
    }

    /// <summary>The page's keys are one set, which the key handler and the guide both read: a key added to it works and is listed.</summary>
    [Fact]
    public void TheCostKeySetDrivesTheKeysAndTheGuide()
    {
        foreach (var key in TuiKeys.CostPage)
        {
            var open = Update(Model(), Letter(key)).Model;
            open.Cost.Should().NotBeNull(key.ToString());
            Update(open, Letter(key)).Model.Cost.Should().BeNull(key.ToString());
        }
        TuiHelp.Groups.Single(group => group.Title == "Anywhere").Keys.Should().Contain(help => help.Key == TuiKeys.Name(TuiKeys.CostPage));
    }
}
