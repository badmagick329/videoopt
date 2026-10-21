using FluentAssertions;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli.Features.Tui;
using static VideoOptimiser.UnitTests.Features.Tui.TuiTestData;

namespace VideoOptimiser.UnitTests.Features.Tui;

public sealed class TuiQueueAdditionUiTests
{
    private static TuiMsg Enter => Press(ConsoleKey.Enter, '\r');
    private static TuiMsg Escape => Press(ConsoleKey.Escape, '\u001b');

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

        step.Model.Choice!.Options.Select(option => option.Label).Should().Equal("Watch folders", "Immich", "File path");
        step.Effects.Should().BeEmpty();
        var selected = Update(step.Model, Enter);
        selected.Effects.Should().Equal(new AddVideos(QueueAdditionSource.WatchFolders));
        selected.Model.Dialog.Should().BeNull();
        selected.Model.Run.Should().Be(model.Run);
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

        step.Effects.Should().Equal(new AddVideos(QueueAdditionSource.File, Path: "C:\\videos\\q s? [holiday].mp4"));
        step.Model.Dialog.Should().BeNull();
        var afterLineFeed = Update(step.Model, Press(ConsoleKey.Enter, '\n'));
        afterLineFeed.Effects.Should().BeEmpty();
        afterLineFeed.Model.Quit.Should().BeFalse();
    }

    [Fact]
    public void BackspaceEditsAndControlCharactersDoNotReachThePath()
    {
        var model = Type(Input(QueueAdditionSource.File), "abc");
        model = After(model, Press(ConsoleKey.Backspace), Press(0, '\0'), Press(0, '\t'), Press(0, '\n'));
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
        var step = Update(Input(QueueAdditionSource.File, new string('a', 32_767)), Letter('x'));

        var input = (TuiTextInput)step.Model.Dialog!;
        input.Text.Should().HaveLength(32_767);
        input.Problem.Should().NotBeNull();
        step.Effects.Should().BeEmpty();
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
        var asked = new AddVideos(QueueAdditionSource.File, Path: "C:\\video.mp4");
        var refused = new QueueAdditionDoneMsg(asked, ActionResult.Say(LogKind.Warning, "The file did not match the rules."), OfferForce: true);
        var step = Update(Model(), refused);
        step.Model.Confirm!.Question.Should().Contain("video.mp4");
        step.Model.Confirm.Detail.Should().Contain(asked.Path);
        step.Model.Confirm!.OnYes.Should().Be(asked with { Force = true });
        Update(step.Model, Letter('y')).Effects.Should().Equal(asked with { Force = true });
        Update(step.Model, Enter).Effects.Should().BeEmpty();

        Update(Model(), refused with { OfferForce = false }).Model.Dialog.Should().BeNull();
        Update(Model(), refused with { Asked = asked with { Force = true } }).Model.Dialog.Should().BeNull();
        Update(Model(), refused with { Asked = new AddVideos(QueueAdditionSource.WatchFolders) }).Model.Dialog.Should().BeNull();
    }

    [Fact]
    public void AForceAnswerNeverReplacesTheQuestionAlreadyOnScreen()
    {
        var question = new TuiConfirm("Drop a video?", "The original stays.", new QuitNow());
        var model = Model() with { Dialog = question, Busy = Model().Busy.Add(AddVideos.Key, "Adding the file.") };
        var asked = new AddVideos(QueueAdditionSource.File, Path: "C:\\video.mp4");

        var step = Update(model, new QueueAdditionDoneMsg(asked, ActionResult.Say(LogKind.Warning, "Rules refused."), OfferForce: true));

        step.Model.Dialog.Should().BeSameAs(question);
        step.Model.Busy.Should().NotContainKey(AddVideos.Key);
        step.Model.Log.Should().Contain(line => line.Text == "Rules refused.");
        step.Model.Notice!.Text.Should().Contain("another was open");
    }

    [Fact]
    public void CancellingAnAdditionSuppressesAForceQuestionAlreadyOnItsWayBack()
    {
        var asked = new AddVideos(QueueAdditionSource.File, Path: "C:\\video.mp4");
        var running = Model() with { Busy = Model().Busy.Add(AddVideos.Key, asked.BusyText) };
        var cancelled = Update(running, Escape).Model;

        var step = Update(cancelled, new QueueAdditionDoneMsg(asked, ActionResult.Say(LogKind.Warning, "Rules refused."), OfferForce: true));

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

        text.Should().Contain("File path").And.Contain("q s?[clip].mp4").And.Contain("enter add").And.Contain("esc cancel");
        text.Should().NotContain(new string('a', 150));
        text.Split('\n').Should().HaveCount(height);
    }

    [Fact]
    public void TheRendererNamesTheBlankCountAndOffersAdditionCancellationInANarrowFrame()
    {
        using var renderer = new FrameRenderer();
        FrameRenderer.Plain(renderer.Frame(Input(QueueAdditionSource.Immich, "1"))!).Should().Contain("blank = all");
        var model = Model() with { Width = 40, Height = 10, Busy = Model().Busy.Add(AddVideos.Key, "Scanning the watch folders.") };
        FrameRenderer.Plain(renderer.Frame(model)!).Split('\n')[^1].Should().Contain("esc cancel add");
    }
}
