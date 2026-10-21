using FluentAssertions;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli.Features.Tui;
using static VideoOptimiser.UnitTests.Features.Tui.TuiTestData;

namespace VideoOptimiser.UnitTests.Features.Tui;

public sealed class TuiQueueAdditionUiTests
{
    private static TuiMsg Enter => Press(ConsoleKey.Enter, '\r');
    private static TuiMsg Escape => Press(ConsoleKey.Escape, '\u001b');
    private static TuiMsg Pasted => new KeyMsg(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), MoreBuffered: true);

    private const string ClipA = "C:\\Videos\\clip-a.mp4";
    private const string ClipB = "C:\\Videos\\clip-b.mp4";
    private const string ClipC = "C:\\Videos\\clip-c.mp4";

    private static TuiModel Input(QueueAdditionSource source, string text = "") => Model() with { Dialog = new TuiTextInput(source, text) };

    private static TuiModel Type(TuiModel model, string text) =>
        text.Aggregate(model, (current, character) => After(current, Press(0, character)));

    private static TuiModel ChooseSource(TuiModel model, int at)
    {
        model = After(model, Letter('a'));
        for (var i = 0; i < at; i++) model = After(model, Press(ConsoleKey.DownArrow));
        return model;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEmptyQueueAndAnActiveRunBothOfferTheSameSources(bool active)
    {
        var model = Model() with { Run = active ? new LiveRunHolder(LockHolders.BackgroundRun, Now, 4242) : null };

        var step = Update(model, Letter('a'));

        step.Model.Choice!.Options.Select(option => option.Label).Should().Equal("Scan your watch folders", "Search Immich", "Add files by path");
        step.Model.Choice.Detail.Should().Be("Adding only queues videos. A run converts them.");
        step.Effects.Should().BeEmpty();
        var selected = Update(step.Model, Enter);
        selected.Effects.Should().Equal(new AddVideos(QueueAdditionSource.WatchFolders));
        selected.Model.Dialog.Should().BeNull();
        selected.Model.Run.Should().Be(model.Run);
    }

    private const string VideosRoot = @"D:\Videos";
    private const string PhoneRoot = @"E:\Phone\Camera";

    private static TuiModel WithSources(QueueSources sources) => Model() with { Sources = sources };

    [Theory]
    [InlineData(0, "No watch folders are set up. Add one to watch.roots in your config.", "")]
    [InlineData(1, "Scans your watch folder now and queues the videos that fit your rules:", VideosRoot)]
    [InlineData(2, "Scans your 2 watch folders now and queues the videos that fit your rules:", VideosRoot + ", " + PhoneRoot)]
    public void TheWatchFolderChoiceNamesHowManyRootsItScansAndWhich(int roots, string explanation, string listed)
    {
        var model = WithSources(new QueueSources([.. new[] { VideosRoot, PhoneRoot }.Take(roots)], ImmichConfigured: false));

        var scan = After(model, Letter('a')).Choice!.Options[0];

        scan.Explanation.Should().Be(explanation);
        scan.ListLine.Should().Be(listed);
    }

    [Theory]
    [InlineData(true, "Asks how many to queue. Each video is downloaded to check it and kept only if queued.")]
    [InlineData(false, "Immich isn't set up. Set immich.baseUrl in your config to use it.")]
    public void TheImmichChoiceSaysWhatItDownloadsOrThatImmichIsNotSetUp(bool configured, string explanation)
    {
        var options = After(WithSources(new QueueSources([], configured)), Letter('a')).Choice!.Options;

        options[1].Explanation.Should().Be(explanation);
        options[2].Explanation.Should().Be("Asks for one or more file paths, one per line, and queues those files.");
    }

    /// <summary>
    /// The dialog's border rows and its text, read from a plain frame: the row holding its top-left corner and title, down to the
    /// row with a bottom-left corner in the same column. Its inner rows are joined with spaces, so a sentence reads whole however
    /// the dialog's width wrapped it.
    /// </summary>
    private static (int Top, int Bottom, string Text) DialogIn(string[] rows, string title)
    {
        var top = Array.FindIndex(rows, row => row.Contains($"╭─ {title} ", StringComparison.Ordinal));
        top.Should().BeGreaterThanOrEqualTo(0, $"a dialog titled {title} is drawn:\n{string.Join('\n', rows)}");
        var left = rows[top].IndexOf($"╭─ {title} ", StringComparison.Ordinal);
        var right = rows[top].IndexOf('╮', left);
        var bottom = Array.FindIndex(rows, top + 1, row => row.Length > left && row[left] == '╰');
        var inner = rows[(top + 1)..bottom].Select(row => row[(left + 1)..right].Trim()).Where(text => text.Length > 0);
        return (top, bottom, string.Join(' ', inner));
    }

    [Theory]
    [InlineData(140, 40, "Choose")]
    [InlineData(40, 10, "Add videos")]
    public void TheHighlightedOptionAloneIsExplainedAndTheDialogKeepsItsHeightAsTheSelectionMoves(int width, int height, string title)
    {
        var model = WithSources(new QueueSources([VideosRoot, PhoneRoot, @"F:\Archive\Family videos\2019"], ImmichConfigured: true)) with { Width = width, Height = height };
        using var renderer = new FrameRenderer();
        string[] labels = ["Scan your watch folders", "Search Immich", "Add files by path"];
        string[] explanations =
        [
            "Scans your 3 watch folders now and queues the videos that fit your rules: D:\\Videos, E:\\Phone",
            "Asks how many to queue. Each video is downloaded to check it and kept only if queued.",
            "Asks for one or more file paths, one per line, and queues those files."
        ];

        var dialogs = Enumerable.Range(0, 3)
            .Select(at => DialogIn(FrameRenderer.Plain(renderer.Frame(ChooseSource(model, at))!).Split('\n'), title))
            .ToList();

        for (var at = 0; at < 3; at++)
        {
            dialogs[at].Text.Should().Contain("▌ " + labels[at]);
            dialogs[at].Text.Should().Contain(explanations[at]);
            foreach (var other in explanations.Where((_, i) => i != at)) dialogs[at].Text.Should().NotContain(other);
        }
        // The roots that don't fit are cut on their one row, never wrapped onto more.
        dialogs[0].Text.Should().Contain("…").And.NotContain("2019");
        dialogs.Select(dialog => (dialog.Top, dialog.Bottom)).Distinct().Should().ContainSingle();
    }

    [Fact]
    public void SourceChoicesOpenTheirInputAndEscapeCancelsWithoutAnEffect()
    {
        var immich = Update(ChooseSource(Model(), 1), Enter);
        immich.Model.Dialog.Should().Be(new TuiTextInput(QueueAdditionSource.Immich, "1"));
        immich.Effects.Should().BeEmpty();

        var file = Update(ChooseSource(Model(), 2), Enter);
        file.Model.Dialog.Should().Be(new TuiTextInput(QueueAdditionSource.File, ""));
        var cancelled = Update(file.Model, Escape);
        cancelled.Model.Dialog.Should().BeNull();
        cancelled.Effects.Should().BeEmpty();
        After(ChooseSource(Model(), 0), Escape).Dialog.Should().BeNull();
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("", null)]
    [InlineData("2147483647", int.MaxValue)]
    public void ImmichAcceptsAPositiveCountOrAnExplicitBlankForAll(string text, int? expected)
    {
        var step = Update(Input(QueueAdditionSource.Immich, text), Enter);

        step.Effects.Should().Equal(new AddVideos(QueueAdditionSource.Immich, Limit: expected));
        step.Model.Busy.Should().ContainKey(AddVideos.Key);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    public void AnInvalidCountStaysInTheFieldAndAddsNothing(string text)
    {
        var step = Update(Input(QueueAdditionSource.Immich, text), Enter);

        step.Effects.Should().BeEmpty();
        var input = step.Model.Dialog.Should().BeOfType<TuiTextInput>().Subject;
        input.Text.Should().Be(text);
        input.Problem.Should().Contain("positive whole number");
    }

    [Fact]
    public void PastedWindowsPathsKeepSpacesAndActionCharactersAndStripTheirSurroundingQuotes()
    {
        var model = Type(Input(QueueAdditionSource.File), " \"C:\\videos\\q s? [holiday].mp4\" ");
        model.Help.Should().BeFalse();
        model.Quit.Should().BeFalse();

        var step = Update(model, Enter);

        step.Effects.Should().Equal(new AddVideos(QueueAdditionSource.File, ["C:\\videos\\q s? [holiday].mp4"]));
        step.Model.Dialog.Should().BeNull();
        var afterLineFeed = Update(step.Model, Press(ConsoleKey.Enter, '\n'));
        afterLineFeed.Effects.Should().BeEmpty();
        afterLineFeed.Model.Quit.Should().BeFalse();
    }

    [Fact]
    public void APastedEnterStartsTheNextPathAndOnlyATypedEnterSubmits()
    {
        var model = Type(Input(QueueAdditionSource.File), ClipA);
        model = Type(After(model, Pasted), ClipB);
        var broken = Update(model, Pasted);
        broken.Effects.Should().BeEmpty();
        model = Type(broken.Model, ClipC);
        ((TuiTextInput)model.Dialog!).Text.Should().Be($"{ClipA}\n{ClipB}\n{ClipC}");

        var step = Update(model, Enter);

        step.Effects.Should().Equal(new AddVideos(QueueAdditionSource.File, [ClipA, ClipB, ClipC]));
        step.Model.Dialog.Should().BeNull();
        Update(Type(Input(QueueAdditionSource.File), ClipA), Enter).Effects.Should().Equal(new AddVideos(QueueAdditionSource.File, [ClipA]));
        // The Immich count has no lines: a pasted Enter submits it as before.
        Update(Input(QueueAdditionSource.Immich, "2"), Pasted).Effects.Should().Equal(new AddVideos(QueueAdditionSource.Immich, Limit: 2));
    }

    [Theory]
    [InlineData(ConsoleKey.Enter, ConsoleModifiers.Control)]
    [InlineData((ConsoleKey)0, (ConsoleModifiers)0)]
    public void ALineFeedIsALineBreakThatBackspaceRemoves(ConsoleKey key, ConsoleModifiers modifiers)
    {
        var step = Update(Input(QueueAdditionSource.File, ClipA), Press(key, '\n', modifiers));

        step.Effects.Should().BeEmpty();
        ((TuiTextInput)step.Model.Dialog!).Text.Should().Be(ClipA + "\n");
        ((TuiTextInput)After(step.Model, Press(ConsoleKey.Backspace)).Dialog!).Text.Should().Be(ClipA);
    }

    [Theory]
    [InlineData("\"C:\\Videos\\clip-a.mp4\" \"C:\\Videos\\clip-b.mp4\"", new[] { ClipA, ClipB })]
    [InlineData("C:\\Videos\\clip-a.mp4\r\n\r\n   \n\"c:\\videos\\CLIP-A.mp4\"\n C:\\Videos\\clip-b.mp4 ", new[] { ClipA, ClipB })]
    [InlineData("\"C:\\My Videos\\clip a.mp4\"\nC:\\My Videos\\clip b.mp4", new[] { "C:\\My Videos\\clip a.mp4", "C:\\My Videos\\clip b.mp4" })]
    public void SubmittedPathsAreSplitIntoLinesAndQuotedSegmentsInOrderEachOnce(string text, string[] expected)
    {
        var step = Update(Input(QueueAdditionSource.File, text), Enter);

        step.Effects.Should().Equal(new AddVideos(QueueAdditionSource.File, expected));
    }

    [Fact]
    public void BackspaceEditsAndControlCharactersDoNotReachThePath()
    {
        var model = Type(Input(QueueAdditionSource.File), "abc");
        model = After(model, Press(ConsoleKey.Backspace), Press(0, '\0'), Press(0, '\t'));
        ((TuiTextInput)model.Dialog!).Text.Should().Be("ab");
        model = After(model, Press(ConsoleKey.Backspace), Press(ConsoleKey.Backspace), Press(ConsoleKey.Backspace));

        var empty = Update(model, Enter);

        empty.Effects.Should().BeEmpty();
        ((TuiTextInput)empty.Model.Dialog!).Problem.Should().Be("Enter a file path.");
        ((TuiTextInput)Type(empty.Model, "x").Dialog!).Problem.Should().BeNull();
    }

    [Theory]
    [InlineData(ConsoleKey.OemMinus, '\\')]
    [InlineData(ConsoleKey.C, 'ć')]
    public void AltGrPrintableCharactersStayInThePath(ConsoleKey key, char character)
    {
        var model = Input(QueueAdditionSource.File, "C:");

        var step = Update(model, Press(key, character, ConsoleModifiers.Control | ConsoleModifiers.Alt));

        ((TuiTextInput)step.Model.Dialog!).Text.Should().Be("C:" + character);
        step.Model.Quit.Should().BeFalse();
        step.Effects.Should().BeEmpty();
    }

    [Fact]
    public void TextInputIsBoundedWithoutDroppingTheDialog()
    {
        // The cap is on the whole entry: a line break counts like a character.
        foreach (var key in new[] { Letter('x'), Pasted })
        {
            var step = Update(Input(QueueAdditionSource.File, new string('a', 32_767)), key);

            var input = (TuiTextInput)step.Model.Dialog!;
            input.Text.Should().HaveLength(32_767);
            input.Problem.Should().NotBeNull();
            step.Effects.Should().BeEmpty();
        }
    }

    [Fact]
    public void BackspaceRemovesAWholeSupplementaryUnicodeCharacter()
    {
        var model = Type(Input(QueueAdditionSource.File), "C:\\holiday😀");

        var step = Update(model, Press(ConsoleKey.Backspace));

        ((TuiTextInput)step.Model.Dialog!).Text.Should().Be("C:\\holiday");
    }

    [Fact]
    public void AnAdditionHoldsItsKeyUntilCompletionAndThenLogsAndRefreshes()
    {
        var asked = new AddVideos(QueueAdditionSource.WatchFolders);
        var running = Update(ChooseSource(Model(), 0), Enter).Model;
        var repeated = Update(running, Letter('a'));
        repeated.Model.Dialog.Should().BeNull();
        repeated.Effects.Should().BeEmpty();

        var completed = Update(running, new QueueAdditionDoneMsg(asked, ActionResult.Say(LogKind.Success, "Added 2 videos.")));

        completed.Model.Busy.Should().NotContainKey(AddVideos.Key);
        completed.Model.Log.Should().Contain(line => line.Text == "Added 2 videos.");
        completed.Model.Notice!.Text.Should().Be("Added 2 videos.");
        completed.Effects.Should().Equal(new RefreshSnapshot());
    }

    [Fact]
    public void OnlyARulesRefusalOffersForceAndAnExplicitYesRetriesThatPath()
    {
        var asked = new AddVideos(QueueAdditionSource.File, [ClipA]);
        var refused = new QueueAdditionDoneMsg(asked, ActionResult.Say(LogKind.Warning, "The file did not match the rules."), [ClipA]);
        var step = Update(Model(), refused);
        step.Model.Confirm!.Question.Should().Be("Add clip-a.mp4 despite the rules?");
        step.Model.Confirm.Detail.Should().Contain(ClipA);
        step.Model.Confirm!.OnYes.Should().Be(asked with { Force = true });
        Update(step.Model, Letter('y')).Effects.Should().Equal(asked with { Force = true });
        Update(step.Model, Enter).Effects.Should().BeEmpty();

        Update(Model(), refused with { ForceCandidates = [] }).Model.Dialog.Should().BeNull();
        Update(Model(), refused with { Asked = asked with { Force = true } }).Model.Dialog.Should().BeNull();
        Update(Model(), refused with { Asked = new AddVideos(QueueAdditionSource.WatchFolders) }).Model.Dialog.Should().BeNull();
    }

    [Fact]
    public void ABatchAsksOnceNamingFiveFilesAndForcesOnlyTheRefusedOnes()
    {
        string[] paths = [.. Enumerable.Range(1, 9).Select(i => $"C:\\Videos\\clip-{i}.mp4")];
        string[] candidates = [.. paths.Skip(2)];
        var asked = new AddVideos(QueueAdditionSource.File, paths);

        var step = Update(Model(), new QueueAdditionDoneMsg(asked, ActionResult.Say(LogKind.Warning, "Queued: 2. Doesn't fit your rules: 7."), candidates));

        var confirm = step.Model.Confirm!;
        confirm.Question.Should().Be("Add 7 files despite the rules?");
        confirm.Detail.Should().StartWith("Files: clip-3.mp4, clip-4.mp4, clip-5.mp4, clip-6.mp4, clip-7.mp4 and 2 more\n")
            .And.EndWith("Force bypasses those rules; AV1 and file safety checks still apply.");
        var forced = new AddVideos(QueueAdditionSource.File, candidates, Force: true);
        Update(step.Model, Letter('y')).Effects.Should().Equal(forced);
        // The forced batch's own refusals never ask again.
        Update(Model(), new QueueAdditionDoneMsg(forced, ActionResult.Say(LogKind.Warning, "Refused."), candidates)).Model.Dialog.Should().BeNull();

        var two = Update(Model(), new QueueAdditionDoneMsg(asked, ActionResult.Say(LogKind.Warning, "Refused."), [ClipA, ClipB])).Model.Confirm!;
        two.Detail.Should().StartWith("Files: clip-a.mp4 and clip-b.mp4\n");
        var one = Update(Model(), new QueueAdditionDoneMsg(asked, ActionResult.Say(LogKind.Warning, "Refused."), [ClipB])).Model.Confirm!;
        one.Question.Should().Be("Add clip-b.mp4 despite the rules?");
        one.OnYes.Should().Be(new AddVideos(QueueAdditionSource.File, [ClipB], Force: true));
    }

    [Fact]
    public void AForceAnswerNeverReplacesTheQuestionAlreadyOnScreen()
    {
        var question = new TuiConfirm("Drop a video?", "The original stays.", new QuitNow());
        var model = Model() with { Dialog = question, Busy = Model().Busy.Add(AddVideos.Key, "Adding the file.") };
        var asked = new AddVideos(QueueAdditionSource.File, [ClipA, ClipB]);

        var step = Update(model, new QueueAdditionDoneMsg(asked, ActionResult.Say(LogKind.Warning, "Rules refused."), [ClipA, ClipB]));

        step.Model.Dialog.Should().BeSameAs(question);
        step.Model.Busy.Should().NotContainKey(AddVideos.Key);
        step.Model.Log.Should().Contain(line => line.Text == "Rules refused.");
        step.Model.Notice!.Text.Should().Be("The force question arrived while another was open. Press a and enter the files again.");
    }

    [Fact]
    public void CancellingAnAdditionSuppressesAForceQuestionAlreadyOnItsWayBack()
    {
        var asked = new AddVideos(QueueAdditionSource.File, [ClipA, ClipB]);
        var running = Model() with { Busy = Model().Busy.Add(AddVideos.Key, asked.BusyText) };
        var cancelled = Update(running, Escape).Model;

        var step = Update(cancelled, new QueueAdditionDoneMsg(asked, ActionResult.Say(LogKind.Warning, "Rules refused."), [ClipA]));

        step.Model.Dialog.Should().BeNull();
        step.Model.Busy.Should().NotContainKey(AddVideos.Key);
    }

    [Fact]
    public void EscapeCancelsAdditionBeforeFinalisationAndKeepsItsBusyKeyUntilItAnswers()
    {
        var model = Model() with
        {
            Finalising = new FinaliseBatch([Id(1), Id(2)], Id(1), 0, 0, 0),
            Busy = Model().Busy.Add(AddVideos.Key, "Scanning the watch folders.")
        };

        var step = Update(model, Escape);

        step.Effects.Should().Equal(new CancelQueueAddition());
        step.Model.Finalising!.StopAsked.Should().BeFalse();
        step.Model.Busy.Should().ContainKey(AddVideos.Key);
        Update(step.Model, Escape).Effects.Should().BeEmpty();
        var stopped = Update(step.Model, new QueueAdditionDoneMsg(new AddVideos(QueueAdditionSource.WatchFolders), ActionResult.Say(LogKind.Info, "Addition cancelled.")));
        Update(stopped.Model, Escape).Effects.Should().Equal(new StopFinalise());
    }

    [Fact]
    public void EscapeClosesAnInputFirstAndControlCKeepsItsQuitMeaning()
    {
        var model = Input(QueueAdditionSource.File) with { Busy = Model().Busy.Add(AddVideos.Key, "Adding.") };
        var escape = Update(model, Escape);
        escape.Model.Dialog.Should().BeNull();
        escape.Effects.Should().BeEmpty();
        var controlC = Press(ConsoleKey.C, '\u0003', ConsoleModifiers.Control);
        var quit = Update(model, controlC);
        quit.Model.Confirm!.OnYes.Should().BeOfType<QuitNow>();
        Update(quit.Model, controlC).Model.Quit.Should().BeTrue();
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(40, 10)]
    public void TheRealRendererClipsLongPathsAndKeepsTheInputInstructionsVisible(int width, int height)
    {
        var model = Input(QueueAdditionSource.File, "C:\\" + new string('a', 150) + "\\q s?[clip].mp4") with { Width = width, Height = height };
        using var renderer = new FrameRenderer();

        var text = FrameRenderer.Plain(renderer.Frame(model)!);

        text.Should().Contain("Add files by path").And.Contain("q s?[clip].mp4").And.Contain("enter add").And.Contain("esc cancel");
        text.Should().NotContain(new string('a', 150));
        text.Split('\n').Should().HaveCount(height);
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(40, 10)]
    public void ThePathBoxShowsTheLastLineAndCountsThePaths(int width, int height)
    {
        var model = Input(QueueAdditionSource.File, $"{ClipA}\n{ClipB}\n{ClipC}\n") with { Width = width, Height = height };
        using var renderer = new FrameRenderer();

        var lines = FrameRenderer.Plain(renderer.Frame(model)!).Split('\n');

        lines.Should().HaveCount(height).And.Contain(line => line.Contains("3 paths"));
        lines.Should().NotContain(line => line.Contains("clip-a.mp4") || line.Contains("Backspace edits"));
        FrameRenderer.Plain(renderer.Frame(model with { Dialog = new TuiTextInput(QueueAdditionSource.File, $"{ClipA}\n{ClipB}") })!).Should().Contain("clip-b.mp4");
    }

    [Fact]
    public void TheRendererNamesTheBlankCountAndOffersAdditionCancellationInANarrowFrame()
    {
        using var renderer = new FrameRenderer();
        FrameRenderer.Plain(renderer.Frame(Input(QueueAdditionSource.Immich, "1"))!).Should().Contain("blank = all").And.Contain("Search Immich");
        var model = Model() with { Width = 40, Height = 10, Busy = Model().Busy.Add(AddVideos.Key, "Scanning the watch folders.") };
        FrameRenderer.Plain(renderer.Frame(model)!).Split('\n')[^1].Should().Contain("esc cancel add");
    }
}
