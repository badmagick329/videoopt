using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli.Features.Tui;
using static VideoOptimiser.UnitTests.Features.Tui.TuiTestData;

namespace VideoOptimiser.UnitTests.Features.Tui;

/// <summary>
/// VOPT-194: a key an action in progress would refuse is greyed in the footer. <see cref="TuiUpdate.BlockedBy"/> is the one
/// check, so each in-scope key is tested three ways against it: blocked exactly while its busy key is held, refused with that
/// text when pressed, and marked in the hints the footer builds.
/// </summary>
public sealed class TuiBlockedKeysTests
{
    private static readonly LiveRunHolder Background = new(LockHolders.BackgroundRun, Now, 4242);

    /// <summary>
    /// A background run going on a managed worker that is up, with a waiting video and two ready ones: every in-scope key's
    /// hint shows in its pane's footer, and nothing but a busy key would refuse most of them.
    /// </summary>
    private static TuiModel Going(Pane focus) => After(Model(), Snap(Snapshot(
        open: [Queued(1), Ready(2), Ready(3)],
        failed: [],
        run: new RunSection(Background, new RunRequests(false, false, WorkerFateChoice.Delete)),
        worker: new WorkerSection(true, "hetzner", AWorker()),
        events: []))) with { Focus = focus, ReviewSelection = Id(2) };

    /// <summary>Every busy key an effect can hold, so a key is shown blocked by its own and by no other.</summary>
    private static readonly string[] EveryBusyKey =
        [BusyKeys.Worker, BusyKeys.Run, BusyKeys.Stop, BusyKeys.Fate, BusyKeys.Dismiss, BusyKeys.Finalise, BusyKeys.Queue(Id(1)), BusyKeys.Queue(Id(2)), AddVideos.Key];

    private static TuiModel Holding(TuiModel model, IEnumerable<string> busyKeys, string? text = null) =>
        busyKeys.Aggregate(model, (current, key) => current with { Busy = current.Busy.SetItem(key, text ?? $"Doing {key}.") });

    /// <summary>A finalise batch of clip-003 alone, so the selected clip-002 is a row outside it.</summary>
    private static TuiModel Finalising(TuiModel model) => Holding(model, [BusyKeys.Finalise], "Finalising.") with
    {
        Finalising = new FinaliseBatch([Id(3)], Id(3), 0, 0, 0)
    };

    private static TuiKey KeyOf(string pane, char c) =>
        TuiKeys.Panes.Single(keys => keys.Title == pane).All.Single(key => key.Is(c));

    private static FooterHint HintOf(TuiModel model, TuiKey key) =>
        TuiView.FooterHints(model).Single(hint => hint.Key == key.Name && hint.What == key.Footer);

    [Theory]
    [InlineData("Queue", "Queue", 'a', AddVideos.Key, "Scanning the watch folders.", "Scanning the watch folders.", false)]
    [InlineData("Anywhere", "Queue", 's', BusyKeys.Run, "Starting the run.", "Starting the run.", false)]
    [InlineData("Anywhere", "Queue", 's', BusyKeys.Worker, DeleteWorker.Text, "The worker is being deleted. Start the run once it's gone.", false)]
    [InlineData("Anywhere", "Activity", 's', BusyKeys.Worker, "Asking Hetzner for the servers.", "Asking Hetzner for the servers.", false)]
    [InlineData("Worker", "Worker", 'c', BusyKeys.Worker, DeleteWorker.Text, DeleteWorker.Text, false)]
    [InlineData("Worker", "Worker", 'd', BusyKeys.Worker, CreateWorker.Text, CreateWorker.Text, false)]
    [InlineData("Anywhere", "Worker", 'x', BusyKeys.Stop, "Asking the run to stop after this video.", "Asking the run to stop after this video.", false)]
    [InlineData("Anywhere", "Review", 'X', BusyKeys.Stop, "Asking the run to carry on.", "Asking the run to carry on.", false)]
    [InlineData("Activity", "Activity", 'w', BusyKeys.Fate, "Changing what happens to the worker.", "Changing what happens to the worker.", false)]
    [InlineData("Review", "Review", 'f', BusyKeys.Finalise, "Reading what is ready to finalise.", "Reading what is ready to finalise.", false)]
    [InlineData("Review", "Review", 'F', BusyKeys.Finalise, "Reading what is ready to finalise.", "Reading what is ready to finalise.", false)]
    [InlineData("Review", "Review", 'f', BusyKeys.Finalise, "Finalising.", "A finalise is going. You can start another once it ends.", true)]
    [InlineData("Review", "Review", 'F', BusyKeys.Finalise, "Finalising.", "A finalise is going. You can start another once it ends.", true)]
    [InlineData("Review", "Review", 'r', BusyKeys.Finalise, "Finalising.", "A finalise is going. You can reject once it ends.", true)]
    public void AKeyIsBlockedExactlyWhileItsBusyKeyIsHeldRefusedWithTheSameTextAndGreyedInTheFooter(
        string table, string focus, char c, string busyKey, string busyText, string expected, bool batch)
    {
        var key = KeyOf(table, c);
        // s answers to two busy keys, so neither may be among the others.
        string[] own = c == 's' ? [BusyKeys.Worker, BusyKeys.Run] : [busyKey];
        var others = Holding(Going(Enum.Parse<Pane>(focus)), EveryBusyKey.Except(own));
        var held = batch ? Finalising(others) : Holding(others, [busyKey], busyText);

        TuiUpdate.BlockedBy(others, key).Should().BeNull();
        HintOf(others, key).Blocked.Should().BeFalse();

        TuiUpdate.BlockedBy(held, key).Should().Be(expected);
        HintOf(held, key).Blocked.Should().BeTrue();
        var pressed = Update(held, Letter(c));
        pressed.Effects.Should().BeEmpty();
        pressed.Model.Dialog.Should().BeNull();
        (pressed.Model.Notice?.Text).Should().Be(expected);
    }

    /// <summary>The footer row of the styled frame, escape sequences kept: the plain text can't tell a greyed key from another.</summary>
    private static string StyledFooter(TuiModel model)
    {
        using var renderer = new FrameRenderer();
        return renderer.Frame(model)!.Split("\r\n")[^1];
    }

    /// <summary>
    /// The colour codes of the run of text holding <paramref name="fragment"/>. Spectre writes neighbouring segments of one
    /// style as one run, so a key and its word in the same run share a style.
    /// </summary>
    private static string StyleOf(string row, string fragment) =>
        System.Text.RegularExpressions.Regex.Matches(row, "\u001b\\[([0-9;]*)m([^\u001b]*)")
            .First(run => run.Groups[2].Value.Contains(fragment, StringComparison.Ordinal)).Groups[1].Value;

    [Fact]
    public void ABlockedHintIsDrawnFaintInItsPlaceAndTheOthersKeepTheirStyle()
    {
        var idle = StyledFooter(Going(Pane.Queue));
        var adding = StyledFooter(Holding(Going(Pane.Queue), [AddVideos.Key], "Scanning the watch folders."));
        const string faint = "38;2;63;70;85";
        const string dim = "38;2;107;114;128";

        StyleOf(idle, " add").Should().Be(dim);
        StyleOf(adding, "a add").Should().Be(faint);
        StyleOf(adding, " hold").Should().Be(dim);
    }

    [Fact]
    public void RejectStaysOpenWhileOnlyTheFinaliseReadIsOut()
    {
        // Reading what is ready holds the finalise key but not the finalise lock, so a reject could always go meanwhile.
        var model = Holding(Going(Pane.Review), [BusyKeys.Finalise], "Reading what is ready to finalise.");

        TuiUpdate.BlockedBy(model, TuiKeys.Review.Reject).Should().BeNull();
        HintOf(model, TuiKeys.Review.Reject).Blocked.Should().BeFalse();
        Update(model, Letter('r')).Model.Dialog.Should().BeOfType<TuiConfirm>();
    }

    private static readonly TuiKey[] InScope =
    [
        TuiKeys.Queue.Add, TuiKeys.Anywhere.Start, TuiKeys.Worker.Create, TuiKeys.Worker.Delete, TuiKeys.Anywhere.StopAfter,
        TuiKeys.Anywhere.StopNow, TuiKeys.Activity.Fate, TuiKeys.Review.Finalise, TuiKeys.Review.FinaliseAll, TuiKeys.Review.Reject
    ];

    [Theory]
    [InlineData("Queue")]
    [InlineData("Worker")]
    [InlineData("Activity")]
    [InlineData("Review")]
    public void AKeyOutOfScopeIsNeverBlockedWhateverIsHeld(string focus)
    {
        // Per-row refusals (a queue verb on a video with its own going) and the keys that are never greyed (? $ q tab arrows esc).
        var model = Finalising(Holding(Going(Enum.Parse<Pane>(focus)), EveryBusyKey));
        var inScope = InScope.Select(key => (key.Name, key.Footer)).ToHashSet();

        foreach (var key in TuiKeys.Panes.SelectMany(keys => keys.All).Where(key => !InScope.Contains(key)))
            TuiUpdate.BlockedBy(model, key).Should().BeNull(key.GuideName);
        TuiView.FooterHints(model).Where(hint => !inScope.Contains((hint.Key, hint.What))).Should().OnlyContain(hint => !hint.Blocked);
    }
}
