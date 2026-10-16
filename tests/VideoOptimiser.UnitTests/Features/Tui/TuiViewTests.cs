using FluentAssertions;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli.Features.Tui;
using VideoOptimiser.Domain.Jobs;
using static VideoOptimiser.UnitTests.Features.Tui.TuiTestData;

namespace VideoOptimiser.UnitTests.Features.Tui;

/// <summary>
/// Frames rendered through the real frame renderer and read as plain text. Each test looks at the lines that matter, not the whole
/// frame: a colour or a border may change, but the pane must still say what it says.
/// </summary>
public sealed class TuiViewTests
{
    private static string[] Rows(TuiModel model)
    {
        using var renderer = new FrameRenderer();
        return FrameRenderer.Plain(renderer.Frame(model)!).Split('\n');
    }

    private static string Text(TuiModel model) => string.Join('\n', Rows(model));

    /// <summary>The row of the frame that holds <paramref name="fragment"/>, so a test reads one line of one pane.</summary>
    private static string Row(string[] rows, string fragment) =>
        rows.FirstOrDefault(row => row.Contains(fragment, StringComparison.Ordinal)) ?? throw new InvalidOperationException($"No row holds '{fragment}':\n{string.Join('\n', rows)}");

    private static readonly string[] PaneTitles = ["Queue", "Worker", "Activity", "Review"];

    private static readonly LiveRunHolder BackgroundRun = new(LockHolders.BackgroundRun, Now, 4242);

    /// <summary>A run converting clip-001 on a worker, as a TUI that has been open a while would show it, built from messages like the real feed's.</summary>
    /// <param name="answers">Events after the reading, such as the worker answering polls.</param>
    private static TuiModel Busy(int width = 140, int height = 40, Pane focus = Pane.Queue, RunEvent[]? answers = null)
    {
        RunEvent[] events =
        [
            Started(),
            Event(RunEventKind.WorkerProgress, text: "The worker video-optimiser-1 is ready."),
            Event(RunEventKind.ConversionStarted, count: 3),
            Progress(JobProgressKind.Header, "[1/3] clip-001.mp4 (1.1 GiB)"),
            Progress(JobProgressKind.Step, "Uploading to the worker."),
            Progress(JobProgressKind.Step, "CRF 27 chosen, encoding."),
            Progress(JobProgressKind.Detail, "38%, 46 fps, about 6m 00s left", percent: 38),
            .. answers ?? []
        ];
        var snapshot = Snapshot(
            open: [Converting(1, remote: true), Queued(2), Queued(3), Held(4), Ready(5, size: 800 * Mib, av1: 352 * Mib, saved: 56m), Ready(6, size: 2000 * Mib, av1: 700 * Mib, saved: 65m)],
            failed: [Failed(7)],
            run: new RunSection(BackgroundRun, new RunRequests(false, false, WorkerFateChoice.Delete)),
            worker: new WorkerSection(true, "hetzner", AWorker()),
            events: events);
        var check = new WorkerCheck(AWorker(status: "running"), 0.0119m, null);
        var model = After(TuiModel.Initial(Now, width, height, "jobs.db") with { Focus = focus }, Snap(snapshot), new WorkerCheckedMsg(0, check), Tick(TimeSpan.Zero, width, height));
        return model;
    }

    // ---- wide -------------------------------------------------------------------------------

    [Fact]
    public void TheQueuePaneListsTheGroupsAndTheRealVideoNames()
    {
        var rows = Rows(Busy());

        Row(rows, "1 converting").Should().Contain("2 in the queue").And.Contain("1 held").And.Contain("2 ready").And.Contain("1 failed");
        Row(rows, "╭─ Queue").Should().Contain("1/7");
        Row(rows, "Converting now").Should().Contain("1");
        Row(rows, "clip-001.mp4  ").Should().Contain("Encoding");
        Row(rows, "Queue, in run order").Should().Contain("2");
        Row(rows, "  1  clip-002.mp4").Should().Contain("400.0 MiB");
        Row(rows, "  2  clip-003.mp4").Should().Contain("400.0 MiB");
        rows.Should().Contain(row => row.Contains("Held  1", StringComparison.Ordinal));
        Row(rows, "clip-004.mp4").Should().Contain("100.0 MiB");
        Row(rows, "clip-005.mp4").Should().Contain("saves 56%");
        Row(rows, "Failed, latest first").Should().NotBeNull();
        Row(rows, "clip-007.mp4").Should().Contain("encoding failed: exit code 1");
    }

    [Fact]
    public void TheWorkerPaneShowsTheNameTypeStatusUptimePriceAndRunningCost()
    {
        var rows = Rows(Busy());

        Row(rows, "video-optimiser-1  ").Should().Contain("running");
        Row(rows, "cx43 · hel1").Should().NotBeNull();
        Row(rows, "Uptime").Should().Contain("1h 12m");
        Row(rows, "Price").Should().Contain("EUR 0.012/h incl. VAT");
        // 72 minutes at EUR 0.0119 an hour.
        Row(rows, "Cost so far").Should().Contain("EUR 0.0143");
    }

    [Fact]
    public void TheActivityPaneShowsTheVideosStageProgressLineStepperAndTheRun()
    {
        var rows = Rows(Busy());

        Row(rows, "Encoding  clip-001.mp4").Should().Contain("on video-optimiser-1");
        Row(rows, "38%, 46 fps").Should().Contain("about 6m 00s left").And.NotContain("no update");
        // The reading's own time left is the only one.
        Row(rows, "38%, 46 fps").Split(" left").Should().HaveCount(2);
        // The bar is a quarter of the pane's inner width, 38% of it filled.
        var bar = Row(rows, "38%, 46 fps").Split('│')[1].Trim().Split(' ')[0];
        bar.Should().MatchRegex("^━+─+$");
        ((double)bar.Count(c => c == '━') / bar.Length).Should().BeApproximately(0.38, 0.04);
        Row(rows, "✓ Uploading").Should().Contain("✓ Finding CRF").And.Contain("● Encoding").And.Contain("○ Downloading").And.Contain("○ Checking");
        Row(rows, "Background run, started").Should().Contain("(process 4242)");
        Row(rows, "╭─ Activity").Should().Contain("following").And.Contain("worker deleted at the end");
        // The log has the steps, and the progress line, which repeats, is not in it.
        Row(rows, "CRF 27 chosen, encoding.").Should().NotBeNull();
        rows.Count(row => row.Contains("38%, 46 fps", StringComparison.Ordinal)).Should().Be(1);
    }

    /// <summary>
    /// A run that has said nothing for a minute says how long, and when the worker last answered, so a slow CRF test clip is
    /// told apart from a lost worker. With no answers to go on (a local run), it says only how long.
    /// </summary>
    [Theory]
    [InlineData(null, "no update for 31m 59s")]
    [InlineData(1910, "worker answered 10s ago")]
    [InlineData(120, "no answer from the worker for 30m 00s")]
    public void AConvertingVideoThatHasBeenQuietForAMinuteSaysHowLongAndWhetherTheWorkerAnswers(int? answeredSecondsAfterNow, string expected)
    {
        RunEvent[] answers = answeredSecondsAfterNow is { } seconds
            ? [new(Now.AddSeconds(seconds), RunEventKind.Progress, Text: string.Empty, Progress: nameof(JobProgressKind.WorkerAnswered), Tone: nameof(JobTone.None))]
            : [];
        // The last event came a second after Now.
        Row(Rows(After(Busy(answers: answers), Tick(TimeSpan.FromSeconds(60), 140, 40))), "38%, 46 fps").Should().Contain("about 6m 00s left").And.NotContain("no update");
        Row(Rows(After(Busy(answers: answers), Tick(TimeSpan.FromMinutes(32), 140, 40))), "38%, 46 fps").Should().MatchRegex(System.Text.RegularExpressions.Regex.Escape(expected) + @"\s*│");
    }

    /// <summary>
    /// Before a step's first line a spinner stands in. A stage whose lines are readings also shows its empty bar, where the
    /// reading's bar will fill; a CRF search's lines have none.
    /// </summary>
    [Theory]
    [InlineData(JobStatus.Encoding, "Encoding", "^─+  [⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]$")]
    [InlineData(JobStatus.CrfSearching, "Finding CRF", "^[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]$")]
    public void BeforeAStepsFirstLineASpinnerStandsIn(JobStatus status, string stage, string pattern)
    {
        RunEvent[] events =
        [
            Started(),
            Event(RunEventKind.ConversionStarted, count: 1),
            Progress(JobProgressKind.Header, "[1/1] clip-001.mp4 (1.1 GiB)"),
            Progress(JobProgressKind.Step, "Already on the worker, checksum verified.")
        ];
        var snapshot = Snapshot(
            open: [Converting(1, status, remote: true)],
            failed: [],
            run: new RunSection(BackgroundRun, new RunRequests(false, false, WorkerFateChoice.Delete)),
            worker: new WorkerSection(true, "hetzner", AWorker()),
            events: events);
        var rows = Rows(After(TuiModel.Initial(Now, 140, 40, "jobs.db"), Snap(snapshot), Tick(TimeSpan.Zero, 140, 40)));

        var header = Array.FindIndex(rows, row => row.Contains($"{stage}  clip-001.mp4", StringComparison.Ordinal));
        rows[header + 1].Split('│')[1].Trim().Should().MatchRegex(pattern);
    }

    /// <summary>A log line wider than the pane wraps under its text instead of losing its end.</summary>
    [Fact]
    public void ALongLogLineWrapsSoItsEndIsShown()
    {
        var words = string.Join(' ', Enumerable.Range(1, 40).Select(n => $"word{n}"));
        var rows = Rows(After(Busy(), new TuiLogMsg(LogKind.Success, words)));

        Row(rows, "word1 word2").Should().NotContain("…");
        Row(rows, "word40").Should().NotBeNull();
        var logRows = rows.Where(row => row.Contains("word", StringComparison.Ordinal)).ToArray();
        logRows.Should().HaveCountGreaterThan(1);
        string.Join(' ', logRows.Select(row => row.Split('│')[1].Trim())).Should().EndWith(words);
    }

    [Fact]
    public void TheReviewPaneShowsOriginalAv1AndSavedPerVideoAndTheTotal()
    {
        var rows = Rows(Busy());

        Row(rows, "Original").Should().Contain("AV1").And.Contain("Saved");
        Row(rows, "clip-005.mp4").Should().NotBeNull();
        var review = rows.Where(row => row.Contains("MiB", StringComparison.Ordinal) && row.Contains("-56%", StringComparison.Ordinal)).ToArray();
        review.Should().ContainSingle().Which.Should().Contain("800.0 MiB").And.Contain("352.0 MiB");
        Row(rows, "-65%").Should().Contain("2.0 GiB").And.Contain("700.0 MiB");
        // 448 MiB and 1300 MiB saved.
        Row(rows, "2 ready to finalise, would save").Should().Contain("1.7 GiB");
    }

    private const string LongWarning = "Audio stream 2 of the original is missing from the AV1, so a second language or commentary track may be gone.";

    /// <summary>Ready with clip-005 clean and clip-006 warned, the Review pane's cursor on <paramref name="selected"/>.</summary>
    private static TuiModel Warned(int selected, int width = 140, int height = 40) =>
        After(TuiModel.Initial(Now, width, height, "jobs.db"), Snap(Quiet([Queued(2), Ready(5), Ready(6, size: 2000 * Mib, av1: 700 * Mib, saved: 65m) with { Warnings = ["The AV1 is 2.1 s shorter than the original.", LongWarning] }])), Tick(TimeSpan.Zero, width, height))
            with { ReviewSelection = Id(selected) };

    /// <summary>VOPT-118: F leaves a warned video out, so both panes mark it and the header counts it, wherever the cursor is.</summary>
    [Fact]
    public void AWarnedReadyVideoIsMarkedInBothPanesAndCountedInTheHeader()
    {
        var rows = Rows(Warned(selected: 5));

        Row(rows, "converting").Should().Contain("2 ready, 1 to check");
        var warned = rows.Where(row => row.Contains("clip-006.mp4", StringComparison.Ordinal)).ToArray();
        warned.Should().HaveCount(2);
        warned[0].Should().Contain("2 warnings · saves 65%");
        warned[1].Should().MatchRegex(@"2 warnings\s+2\.0 GiB\s+700\.0 MiB\s+-65%");
        rows.Where(row => row.Contains("clip-005.mp4", StringComparison.Ordinal)).Should().HaveCount(2).And.OnlyContain(row => !row.Contains("warning", StringComparison.Ordinal));
    }

    /// <summary>The selected video's warnings show whole in the Review pane, a long one wrapped; a clean video shows none.</summary>
    [Theory]
    [InlineData(6, true)]
    [InlineData(5, false)]
    public void TheSelectedVideosWarningsShowInTheReviewPane(int selected, bool shown)
    {
        var rows = Rows(Warned(selected));

        // The right column starts at 55% of the width; the rows of the left one would hold the queue's names.
        var review = rows.Select(row => row[77..].Trim().Trim('│').Trim()).ToArray();
        if (!shown)
        {
            review.Should().NotContain(cell => cell.Contains("Validation warnings:", StringComparison.Ordinal) || cell.Contains("shorter", StringComparison.Ordinal));
            return;
        }
        var heading = Array.IndexOf(review, "Validation warnings:");
        heading.Should().BePositive();
        review[heading + 1].Should().Be("The AV1 is 2.1 s shorter than the original.");
        // The long one wraps under its text, whole, and the pinned summary still follows.
        string.Join(' ', review.Skip(heading + 2).TakeWhile(cell => !cell.StartsWith("2 ready to finalise", StringComparison.Ordinal))).Should().Be(LongWarning);
        review.Should().NotContain(cell => cell.Contains('…'));
    }

    /// <summary>A short pane keeps the list's rows first: the warnings keep their first rows and end in an ellipsis where cut.</summary>
    [Fact]
    public void OnAShortPaneTheWarningsGiveWayToTheList()
    {
        // Stacked and focused: Review gets the 7 rows left after three collapsed panes, too few for both warnings beside the list.
        var rows = Rows(Warned(selected: 6, width: 80, height: 20) with { Focus = Pane.Review });

        Row(rows, "clip-005.mp4").Should().Contain("%");
        Row(rows, "clip-006.mp4").Should().Contain("-65%");
        Row(rows, "Validation warnings:").Should().NotBeNull();
        Row(rows, "The AV1 is 2.1 s shorter than the original.").Should().NotContain("…");
        Row(rows, "Audio stream 2").Should().MatchRegex(@"…\s*│$");
        rows.Should().NotContain(row => row.Contains("commentary track may be gone.", StringComparison.Ordinal));
        Row(rows, "2 ready to finalise, would save").Should().NotBeNull();
    }

    [Fact]
    public void TheFooterListsTheFocusedPanesKeys()
    {
        Row(Rows(Busy(focus: Pane.Queue)), " Queue  ").Should().Contain("↑↓ select").And.Contain("tab").And.Contain("? help").And.Contain("q quit");
        Row(Rows(Busy(focus: Pane.Worker)), " Worker  ").Should().Contain("r check now");
        Row(Rows(Busy(focus: Pane.Activity)), " Activity  ").Should().Contain("end follow");
    }

    // ---- stacked ----------------------------------------------------------------------------

    /// <summary>Under 100 columns the four panes stack, each full width, and still show the same facts.</summary>
    [Fact]
    public void ANarrowWindowStacksTheFourPanesAndKeepsTheirFacts()
    {
        var rows = Rows(Busy(width: 80, height: 30));

        rows.Should().HaveCount(30).And.OnlyContain(row => row.Length == 80);
        var titles = PaneTitles.Select(title => Array.FindIndex(rows, row => row.StartsWith($"╭─ {title}", StringComparison.Ordinal))).ToArray();
        titles.Should().OnlyContain(index => index >= 0).And.BeInAscendingOrder();

        Row(rows, "  1  clip-002.mp4").Should().Contain("400.0 MiB");
        Row(rows, "video-optimiser-1  ").Should().Contain("running");
        Row(rows, "Encoding  clip-001.mp4").Should().Contain("on video-optimiser-1");
        Row(rows, "38%, 46 fps").Should().Contain("about 6m 00s left");
        Row(rows, "Background run, started").Should().Contain("(process 4242)");
        // The queue pane is four rows tall here and scrolls to the cursor, so the ready video shows in the review pane.
        Row(rows, "clip-005.mp4").Should().Contain("800.0 MiB").And.Contain("352.0 MiB").And.Contain("-56%");
    }

    /// <summary>In a short window the panes that don't have focus shrink to one summary row, and the focused one gets the room.</summary>
    [Fact]
    public void AShortWindowCollapsesTheUnfocusedPanesToOneRowEach()
    {
        var rows = Rows(Busy(width: 80, height: 22, focus: Pane.Activity));

        Row(rows, "1 converting, 2 in the queue, 1 held").Should().NotBeNull();
        Row(rows, "video-optimiser-1, running").Should().Contain("EUR 0.0143");
        Row(rows, "2 ready to finalise").Should().Contain("1.7 GiB to save");
        Row(rows, "Encoding  clip-001.mp4").Should().NotBeNull();
    }

    [Fact]
    public void AWindowTooSmallSaysSo()
    {
        Text(Busy(width: 39, height: 8)).Should().Contain("Window too small: 39x8. Needs 40x10.");
    }

    // ---- states -----------------------------------------------------------------------------

    [Fact]
    public void BeforeTheFirstReadThePanesSayTheyAreReading()
    {
        var text = Text(TuiModel.Initial(Now, 140, 40, "jobs.db"));

        text.Should().Contain("Reading the queue.").And.Contain("Reading the worker.");
    }

    [Fact]
    public void ALifecycleThatIsNotHetznerSaysThereIsNoManagedWorkerAndWhyInOneLine()
    {
        var model = After(TuiModel.Initial(Now, 140, 40, "jobs.db"), Snap(Quiet([Queued(1)])));

        Row(Rows(model), "No managed worker").Should().Contain("No managed worker: remoteSsh.lifecycle is \"manual\".");
    }

    /// <summary>The worker pane says what Hetzner could and couldn't tell, so a billing worker never looks like no worker.</summary>
    [Theory]
    [InlineData("none", "No worker", "Nothing is billing.")]
    [InlineData("asking", "No worker", "Asking Hetzner.")]
    [InlineData("failed", "No worker", "couldn't check: Hetzner timed out after 15 seconds.")]
    public void AManagedLifecycleWithNoWorkerSaysWhatItKnows(string state, string heading, string detail)
    {
        var check = state switch
        {
            "none" => new WorkerCheck(null, null, null),
            "failed" => new WorkerCheck(null, null, "Hetzner timed out after 15 seconds."),
            _ => null
        };
        var model = After(TuiModel.Initial(Now, 140, 40, "jobs.db"), Snap(Snapshot([], [], new RunSection(null, null), new WorkerSection(true, "hetzner", null), [])));
        if (check is not null) model = After(model, new WorkerCheckedMsg(0, check));

        var rows = Rows(model);

        Row(rows, heading).Should().Contain("○");
        Row(rows, detail).Should().NotBeNull();
    }

    [Fact]
    public void AWorkerHetznerCouldNotBeAskedAboutShowsFromTheStateFileWithTheReason()
    {
        var model = After(TuiModel.Initial(Now, 140, 40, "jobs.db"),
            Snap(Snapshot([], [], new RunSection(null, null), new WorkerSection(true, "hetzner", AWorker()), [])),
            new WorkerCheckedMsg(0, new WorkerCheck(AWorker(), null, "no Hetzner token is set.")));

        var rows = Rows(model);

        Row(rows, "video-optimiser-1  ").Should().Contain("as recorded on this PC");
        Row(rows, "Price").Should().Contain("unknown");
        Row(rows, "Hetzner  ").Should().Contain("couldn't check: no Hetzner token is set.");
    }

    [Fact]
    public void ABootstrappingWorkerIsFlaggedInThePaneTitle()
    {
        var model = After(TuiModel.Initial(Now, 140, 40, "jobs.db"),
            Snap(Snapshot([], [], new RunSection(null, null), new WorkerSection(true, "hetzner", AWorker(status: RemoteWorkerStatuses.Bootstrapping)), [])));

        Row(Rows(model), "╭─ Worker").Should().Contain("bootstrapping");
        Row(Rows(model), "video-optimiser-1").Should().Contain("bootstrapping, not ready for jobs yet");
    }

    /// <summary>The idle line says why nothing converts, from the run and what it has reported.</summary>
    [Theory]
    [InlineData("norun", "No run is going.")]
    [InlineData("waiting", "The run is waiting for the worker.")]
    [InlineData("ending", "The run is ending.")]
    [InlineData("empty", "The queue is empty.")]
    [InlineData("between", "The run is between videos.")]
    public void TheIdleLineSaysWhyNothingConverts(string state, string expected)
    {
        RunEvent[] events = state switch
        {
            "waiting" => [Started(), Event(RunEventKind.WorkerProgress, text: "Creating Hetzner cx43 in hel1.")],
            "ending" => [Started(), Event(RunEventKind.ConversionStarted, count: 1), Event(RunEventKind.ConversionEnded)],
            "empty" or "between" => [Started(), Event(RunEventKind.ConversionStarted, count: 1)],
            _ => []
        };
        var queue = state == "between" ? new[] { Queued(2) } : [];
        var run = state == "norun" ? null : BackgroundRun;
        var model = After(TuiModel.Initial(Now, 140, 40, "jobs.db"), Snap(Snapshot(queue, [], new RunSection(run, null), new WorkerSection(false, "manual", null), events)));

        Row(Rows(model), "Idle").Should().Contain(expected);
    }

    /// <summary>A video converting on this PC has no upload or download stage to show.</summary>
    [Fact]
    public void ALocalVideosStepperLeavesOutTheUploadAndDownload()
    {
        var model = After(TuiModel.Initial(Now, 140, 40, "jobs.db"), Snap(Snapshot([Converting(1, JobStatus.CrfSearching, remote: false)], [], new RunSection(BackgroundRun, null), new WorkerSection(false, "manual", null), [Started()])));

        var rows = Rows(model);

        Row(rows, "Finding CRF  clip-001.mp4").Should().Contain("on this PC");
        Row(rows, "● Finding CRF").Should().Contain("○ Encoding").And.Contain("○ Checking").And.NotContain("Uploading").And.NotContain("Downloading");
    }

    [Fact]
    public void AForegroundRunShowsItsCommandAndNoWorkerFate()
    {
        var holder = new LiveRunHolder(LockHolders.ImmichRun, Now, 777);
        var model = After(TuiModel.Initial(Now, 140, 40, "jobs.db"), Snap(Snapshot([], [], new RunSection(holder, null), new WorkerSection(true, "hetzner", null), [Started(holder: LockHolders.ImmichRun, fate: null)])));

        Row(Rows(model), "Foreground run").Should().Contain("(immich run)").And.Contain("(process 777)");
        Row(Rows(model), "╭─ Activity").Should().NotContain("worker");
    }

    /// <summary>The pane title carries what the run will do about the worker, until a stop is asked: then the stop is the news.</summary>
    [Theory]
    [InlineData(false, false, "Keep", "worker kept at the end")]
    [InlineData(false, false, "Delete", "worker deleted at the end")]
    [InlineData(true, false, "Keep", "stopping after this video")]
    [InlineData(false, true, "Delete", "stopping now")]
    public void TheActivityTitleSaysTheWorkerFateOrAStopAskedOfTheRun(bool stopAfter, bool stopNow, string fate, string expected)
    {
        var requests = new RunRequests(stopAfter, stopNow, Enum.Parse<WorkerFateChoice>(fate));
        var model = After(Busy(), Snap(Snapshot(run: new RunSection(BackgroundRun, requests))));

        Row(Rows(model), "╭─ Activity").Should().Contain(expected);
    }

    /// <summary>The requests row answers once the run has reset it; until then the event file's RunStarted has the fate the run began with.</summary>
    [Fact]
    public void TheWorkerFateComesFromTheRunStartedEventUntilTheRequestsAreRead()
    {
        var model = After(Busy(), Snap(Snapshot(run: new RunSection(BackgroundRun, null))));

        Row(Rows(model), "╭─ Activity").Should().Contain("worker deleted at the end");
    }

    // ---- footer and dialog ------------------------------------------------------------------

    [Fact]
    public void AReadProblemShowsInTheFooterAndANoticeTakesItsPlace()
    {
        var model = After(Busy(), Snap(Snapshot(problems: "Couldn't read the queue: database is locked.")));
        Row(Rows(model), "Couldn't read the queue").Should().Contain("database is locked.");

        model = After(model with { Focus = Pane.Worker }, Letter('r'));
        var footer = Rows(model)[^1];
        footer.Should().Contain("Hetzner").And.NotContain("database is locked");
    }

    [Fact]
    public void ADialogIsDrawnOverTheDimmedScreenWithItsQuestionAndTheYesNoFooter()
    {
        var model = Busy() with { Confirm = new TuiConfirm("Check the worker now?", "It asks Hetzner, which can take 15 seconds.", new RefreshWorkerCheck(0)) };

        var rows = Rows(model);

        Row(rows, "Confirm").Should().Contain("╭");
        Row(rows, "Check the worker now?").Should().NotBeNull();
        Row(rows, "[y/N]").Should().Contain("y confirms, any other key cancels");
        rows[^1].Should().Contain("y confirm").And.Contain("any other key cancel");
        // The screen behind it is still there.
        Row(rows, "clip-002.mp4").Should().NotBeNull();
    }

    /// <summary>A list dialog: every option on its own line with its hint, the selected one marked, over the dimmed screen.</summary>
    [Fact]
    public void AListDialogShowsItsOptionsWithTheirPricesAndTheSelectionAndItsKeys()
    {
        var choice = new TuiChoice("Which worker converts?", "Pick the server the run uses. Prices include VAT.",
            [new ChoiceOption("cx43  hel1", null, Hint: "EUR 0.012/h"), new ChoiceOption("cx53  fsn1", null, Hint: "EUR 0.024/h"), new ChoiceOption("No worker: convert on this PC", null)], 1);
        var model = Busy() with { Choice = choice };

        var rows = Rows(model);

        Row(rows, "Choose").Should().Contain("╭");
        Row(rows, "Which worker converts?").Should().NotBeNull();
        Row(rows, "cx43  hel1").Should().Contain("EUR 0.012/h").And.NotContain("▌ cx43");
        Row(rows, "cx53  fsn1").Should().Contain("▌ cx53  fsn1").And.Contain("EUR 0.024/h");
        Row(rows, "No worker: convert on this PC").Should().NotContain("▌ No worker");
        Row(rows, "enter choose").Should().Contain("↑↓ move").And.Contain("esc cancel");
        rows[^1].Should().Contain("↑↓ move").And.Contain("enter choose").And.Contain("esc cancel").And.NotContain("tab");
        Row(rows, "clip-002.mp4").Should().NotBeNull();
    }

    /// <summary>Each pane's footer lists the keys it answers, and s starts a run from all of them.</summary>
    [Theory]
    [InlineData("Queue", new[] { "h hold", "r release", "n run next", "d drop", "t retry", "x stop after", "X stop now", "s start run" })]
    [InlineData("Worker", new[] { "c create", "d delete", "r check now", "x stop after", "X stop now", "s start run" })]
    [InlineData("Activity", new[] { "w worker fate", "x stop after", "X stop now", "s start run" })]
    [InlineData("Review", new[] { "o original", "a AV1", "f finalise", "F all", "r reject", "x stop after", "X stop now", "s start run" })]
    public void EachPanesFooterListsItsActionKeys(string paneName, string[] keys)
    {
        var footer = Rows(Busy(focus: Enum.Parse<Pane>(paneName)))[^1];

        foreach (var key in keys) footer.Should().Contain(key);
        // The guide is offered before quit, so it survives as long as quit does.
        footer.Should().Contain("? help").And.Contain("q quit");
        footer.IndexOf("? help", StringComparison.Ordinal).Should().BeLessThan(footer.IndexOf("q quit", StringComparison.Ordinal));
        // Reject (VOPT-112) is the Review pane's key alone.
        if (paneName != "Review") footer.Should().NotContain("reject");
    }

    /// <summary>The stop keys are in every footer while a run is going, and only then: with none there is nothing to stop.</summary>
    [Fact]
    public void TheStopKeysShowInEveryFooterOnlyWhileARunIsGoing()
    {
        foreach (var pane in Enum.GetValues<Pane>())
        {
            Rows(Busy(focus: pane))[^1].Should().Contain("x stop after").And.Contain("X stop now");
            var idle = After(Busy(focus: pane), Snap(Snapshot(run: new RunSection(null, null))));
            Rows(idle)[^1].Should().NotContain("stop after").And.NotContain("X stop now");
        }
    }

    /// <summary>With no managed worker the fate means nothing, so the title leaves it out.</summary>
    [Fact]
    public void TheActivityTitleLeavesOutTheWorkerFateWhenThereIsNoManagedWorker()
    {
        var model = After(Busy(), Snap(Snapshot(worker: new WorkerSection(false, "manual", null))));

        Row(Rows(model), "╭─ Activity").Should().Contain("following").And.NotContain("worker");
    }

    /// <summary>While the worker is created or deleted the pane says so on its last row, and the footer says what is being done.</summary>
    [Fact]
    public void AWorkerActionInFlightShowsInTheWorkerPaneAndTheFooter()
    {
        var model = Update(After(Busy(), Press(ConsoleKey.Tab)), Letter('d')).Model;
        model = Update(model, Letter('y')).Model;

        var rows = Rows(model);

        Row(rows, "Deleting the worker.").Should().NotBeNull();
        rows[^1].Should().Contain("Deleting the worker.");
    }

    private static TuiModel Guide(int width, int height) => Busy(width, height) with { Help = true };

    [Fact]
    public void TheGuideListsEveryKeyInGroupsAndSaysWhenAKeyNeedsAState()
    {
        var rows = Rows(Guide(140, 40));

        Row(rows, "╭").Should().NotBeNull();
        Row(rows, "Keys").Should().Contain("╭");
        foreach (var group in new[] { "Anywhere", "Queue", "Worker", "Activity", "Review" })
            rows.Should().Contain(row => row.Contains($"│ {group}", StringComparison.Ordinal) || row.Contains($"  {group} ", StringComparison.Ordinal), group);
        Row(rows, "tab / shift+tab").Should().Contain("next or previous pane");
        Row(rows, "stop the run after this video").Should().Contain("(while a run is going)");
        Row(rows, "release a held video").Should().NotBeNull();
        Row(rows, "create a worker").Should().Contain("(managed worker only)");
        Row(rows, "delete or keep the worker when the run ends").Should().Contain("(background run");
        Row(rows, "finalise every ready video").Should().NotBeNull();
        Row(rows, "reject the selected video").Should().Contain("after asking");
        Row(rows, "Any key closes this.").Should().NotBeNull();
        rows[^1].Should().Contain("any key close the help");
        // The screen behind it is still there, dimmed.
        Row(rows, "clip-002.mp4").Should().NotBeNull();
    }

    /// <summary>In a window too short for every row the per-pane groups drop from the end, and the Anywhere group stays.</summary>
    [Fact]
    public void AShortWindowShowsWhatFitsAndDropsThePerPaneGroupsFromTheEnd()
    {
        var rows = Rows(Guide(140, 22));

        Row(rows, "tab / shift+tab").Should().NotBeNull();
        Row(rows, "release a held video").Should().NotBeNull();
        rows.Should().NotContain(row => row.Contains("finalise every ready video", StringComparison.Ordinal));
        rows.Should().NotContain(row => row.Contains("create a worker", StringComparison.Ordinal));
        rows.Should().HaveCount(22);

        var tiny = Rows(Guide(140, 12));
        Row(tiny, "tab / shift+tab").Should().NotBeNull();
        tiny.Should().NotContain(row => row.Contains("release a held video", StringComparison.Ordinal));
    }

    // ---- real file names --------------------------------------------------------------------

    /// <summary>File names come from real folders: wide glyphs take two cells, so a row built by length would push the pane's border out.</summary>
    [Fact]
    public void WideGlyphsInAFileNameDoNotBreakTheLayout()
    {
        var model = After(TuiModel.Initial(Now, 140, 40, "jobs.db"), Snap(Quiet([Queued(1, name: "休日の動画-はじめての旅行-0001.mp4"), Queued(2, name: "clip-with-a-very-long-name-that-has-to-be-cut-before-the-size-column-0002.mp4")])));

        var rows = Rows(model);

        rows.Should().OnlyContain(row => TuiText.Cells(row) == 140);
        Row(rows, "休日").Should().Contain("400.0 MiB");
    }
}
