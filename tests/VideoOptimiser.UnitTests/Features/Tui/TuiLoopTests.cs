using System.Threading.Channels;
using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli.Features.Tui;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;
using static VideoOptimiser.UnitTests.Features.Tui.TuiTestData;

namespace VideoOptimiser.UnitTests.Features.Tui;

/// <summary>The loop around Update: one channel in, frames and effects out, with the console and the clock swapped for scripts.</summary>
public sealed class TuiLoopTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static Channel<TuiMsg> NewChannel() => Channel.CreateUnbounded<TuiMsg>(new UnboundedChannelOptions { SingleReader = true });

    private static async Task<T> FirstAsync<T>(ChannelReader<T> reader)
    {
        using var timeout = new CancellationTokenSource(Patience);
        return await reader.ReadAsync(timeout.Token);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    /// <summary>
    /// What Update asks for starts after the frame is drawn, and the loop ends on q with the model it reached: the screen is
    /// drawn once for the first model and again only when something changed.
    /// </summary>
    [Fact]
    public async Task ThePumpDrawsFramesStartsTheEffectsUpdateAskedForAndEndsOnQ()
    {
        var channel = NewChannel();
        var frames = new List<string>();
        var effects = new List<TuiEffect>();
        var managed = Snapshot([], [], new RunSection(null, null), new WorkerSection(true, "hetzner", AWorker()), []);
        channel.Writer.TryWrite(Snap(managed));
        channel.Writer.TryWrite(Tick(TimeSpan.FromSeconds(1)));

        var pump = TuiLoop.PumpAsync(channel.Reader, TuiModel.Initial(Now, 140, 40, "jobs.db"), frames.Add, effects.Add, CancellationToken.None);
        await UntilAsync(() => effects.Count > 0);
        channel.Writer.TryWrite(Letter('q'));
        var last = await pump.WaitAsync(Patience);

        effects.Should().Equal(new RefreshWorkerCheck(0));
        last.Quit.Should().BeTrue();
        frames.Should().NotBeEmpty();
        FrameRenderer.Plain(frames[^1]).Should().Contain("video-optimiser-1");
    }

    /// <summary>Ctrl+Break cancels the token: the loop leaves quietly and the caller restores the terminal.</summary>
    [Fact]
    public async Task ThePumpLeavesWhenItsTokenIsCancelled()
    {
        var channel = NewChannel();
        using var cancel = new CancellationTokenSource();

        var pump = TuiLoop.PumpAsync(channel.Reader, TuiModel.Initial(Now, 140, 40, "jobs.db"), _ => { }, _ => { }, cancel.Token);
        await cancel.CancelAsync();

        (await pump.WaitAsync(Patience)).Quit.Should().BeFalse();
    }

    /// <summary>A held key must not queue up frames: everything waiting is applied, then one frame is drawn.</summary>
    [Fact]
    public async Task ThePumpAppliesEveryWaitingMessageBeforeItDrawsAgain()
    {
        var channel = NewChannel();
        var frames = new List<string>();
        channel.Writer.TryWrite(Snap(Quiet([Queued(1), Queued(2), Queued(3), Queued(4)])));
        for (var i = 0; i < 3; i++) channel.Writer.TryWrite(Press(ConsoleKey.DownArrow));
        channel.Writer.TryWrite(Letter('q'));

        var last = await TuiLoop.PumpAsync(channel.Reader, TuiModel.Initial(Now, 140, 40, "jobs.db"), frames.Add, _ => { }, CancellationToken.None).WaitAsync(Patience);

        last.QueueSelection.Should().Be(Id(4));
        // The first model's frame only: q was in the same batch, so nothing after it was drawn.
        frames.Should().ContainSingle();
    }

    // ---- effects ----------------------------------------------------------------------------

    /// <summary>The question runs off the loop thread and its answer comes back as a message that says which question it answers.</summary>
    [Fact]
    public async Task AWorkerCheckEffectPostsHetznersAnswerBackAsAMessage()
    {
        using var rig = new TuiRig();
        rig.Worker.Check = new WorkerCheck(CliFakes.Worker with { Status = "running" }, 0.0119m, null);
        var channel = NewChannel();

        new TuiEffects(rig.Reader, rig.Actions, rig.Configuration, channel.Writer).Run(new RefreshWorkerCheck(7), CancellationToken.None);

        var message = await FirstAsync(channel.Reader);
        message.Should().BeOfType<WorkerCheckedMsg>().Which.Should().Match<WorkerCheckedMsg>(answer => answer.Generation == 7 && answer.Check.HourlyGross == 0.0119m);
    }

    [Fact]
    public async Task AWorkerCheckThatFailsPostsTheReasonAndNeverEndsTheLoop()
    {
        using var rig = new TuiRig();
        rig.Worker.CheckFailure = new InvalidOperationException("The state file is unreadable.");
        var channel = NewChannel();

        new TuiEffects(rig.Reader, rig.Actions, rig.Configuration, channel.Writer).Run(new RefreshWorkerCheck(0), CancellationToken.None);

        (await FirstAsync(channel.Reader)).Should().BeOfType<WorkerCheckedMsg>().Which.Check.NotCheckedReason.Should().Be("The state file is unreadable.");
    }

    /// <summary>A queue verb runs off the loop thread and its words come back under the key the effect held, so Update frees it.</summary>
    [Fact]
    public async Task AnActionEffectPostsItsWordsUnderTheKeyItHeld()
    {
        using var rig = new TuiRig();
        var job = await rig.QueuedAsync("clip-a.mp4");
        var channel = NewChannel();
        var effect = new QueueActionEffect(QueueAction.Hold, job.Id, "clip-a.mp4");

        new TuiEffects(rig.Reader, rig.Actions, rig.Configuration, channel.Writer).Run(effect, CancellationToken.None);

        var message = (ActionDoneMsg)await FirstAsync(channel.Reader);
        message.Key.Should().Be(effect.BusyKey);
        message.Result.Lines.Should().ContainSingle().Which.Text.Should().Be("Held clip-a.mp4. Runs pass over it until it's released.");
    }

    /// <summary>The line a long action reports while it works (a worker's creation) is a message of its own, before the action ends.</summary>
    [Fact]
    public async Task AWorkerCreationPostsItsProgressLinesBeforeItsResult()
    {
        using var rig = new TuiRig();
        rig.Worker.Exists = false;
        var channel = NewChannel();

        new TuiEffects(rig.Reader, rig.Actions, rig.Configuration, channel.Writer).Run(new CreateWorker(new RemoteWorkerCreationOption("cx43", "hel1", 0.0119m)), CancellationToken.None);

        (await FirstAsync(channel.Reader)).Should().Be(new TuiLogMsg(LogKind.Info, "Creating Hetzner cx43 in hel1."));
        (await FirstAsync(channel.Reader)).Should().BeOfType<ActionDoneMsg>().Which.Key.Should().Be("worker");
    }

    /// <summary>Another command changed the video between the read and the write: say so and let the refresh show how it stands.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AVideoAnotherCommandChangedIsSaidSoAndNeverACrash(bool rejecting)
    {
        using var rig = new TuiRig(services => services.AddSingleton<IQueueVerbs>(new ThrowingVerbs(new JobChangedException(Id(1)))));
        var job = await rig.QueuedAsync("clip-a.mp4");
        var channel = NewChannel();

        new TuiEffects(rig.Reader, rig.Actions, rig.Configuration, channel.Writer).Run(new QueueActionEffect(rejecting ? QueueAction.Reject : QueueAction.Hold, job.Id, "clip-a.mp4"), CancellationToken.None);

        var result = ((ActionDoneMsg)await FirstAsync(channel.Reader)).Result;
        result.Lines.Should().ContainSingle().Which.Should().Be(new LogLine(LogKind.Warning, "Another command changed that video just now. The list is refreshing; try again."));
    }

    [Fact]
    public async Task AnyOtherFailureOfAnActionIsAnErrorLineWithItsReason()
    {
        using var rig = new TuiRig(services => services.AddSingleton<IQueueVerbs>(new ThrowingVerbs(new IOException("The database is locked.\nTry again."))));
        var job = await rig.QueuedAsync("clip-a.mp4");
        var channel = NewChannel();

        new TuiEffects(rig.Reader, rig.Actions, rig.Configuration, channel.Writer).Run(new QueueActionEffect(QueueAction.Drop, job.Id, "clip-a.mp4"), CancellationToken.None);

        var done = (ActionDoneMsg)await FirstAsync(channel.Reader);
        done.Key.Should().StartWith("queue:");
        done.Result.Lines.Should().ContainSingle().Which.Should().Be(new LogLine(LogKind.Error, "That didn't work: The database is locked."));
    }

    [Fact]
    public async Task ARefreshEffectPostsTheSnapshotNow()
    {
        using var rig = new TuiRig();
        await rig.QueuedAsync("clip-a.mp4");
        var channel = NewChannel();

        new TuiEffects(rig.Reader, rig.Actions, rig.Configuration, channel.Writer).Run(new RefreshSnapshot(), CancellationToken.None);

        (await FirstAsync(channel.Reader)).Should().BeOfType<SnapshotMsg>().Which.Snapshot.Open.Should().ContainSingle();
    }

    private sealed class ThrowingVerbs(Exception failure) : IQueueVerbs
    {
        public Task<QueueVerbOutcome> HoldAsync(string databasePath, JobRecord job, CancellationToken cancellationToken) => Task.FromException<QueueVerbOutcome>(failure);
        public Task<QueueVerbOutcome> ReleaseAsync(string databasePath, JobRecord job, CancellationToken cancellationToken) => Task.FromException<QueueVerbOutcome>(failure);
        public Task<QueueVerbOutcome> NextAsync(string databasePath, JobRecord job, CancellationToken cancellationToken) => Task.FromException<QueueVerbOutcome>(failure);
        public Task<QueueVerbOutcome> DropAsync(AppSettings settings, JobRecord job, CancellationToken cancellationToken) => Task.FromException<QueueVerbOutcome>(failure);
        public Task<QueueVerbOutcome> RejectAsync(AppSettings settings, JobRecord job, CancellationToken cancellationToken) => Task.FromException<QueueVerbOutcome>(failure);
        public IAsyncEnumerable<DropAllStep> DropAllAsync(string databasePath, CancellationToken cancellationToken) => throw failure;
        public Task<QueueVerbOutcome> DismissAsync(string databasePath, JobRecord job, CancellationToken cancellationToken) => Task.FromException<QueueVerbOutcome>(failure);
        public Task<int> DismissAllAsync(string databasePath, CancellationToken cancellationToken) => Task.FromException<int>(failure);
        public Task<LeftFiles?> LeftFilesAsync(AppSettings settings, JobRecord job, bool finished, CancellationToken cancellationToken) => throw failure;
        public Task<bool> CleanupDeletesFilesAsync(AppSettings settings, JobRecord job, CancellationToken cancellationToken) => throw failure;
    }

    // ---- the feed ---------------------------------------------------------------------------

    [Fact]
    public async Task TheFeedPostsASnapshotAtOnceAndKeepsPostingUntilCancelled()
    {
        var channel = NewChannel();
        var reads = 0;
        using var cancel = new CancellationTokenSource();

        TuiLoop.StartFeed(channel.Writer, _ =>
        {
            reads++;
            return Task.FromResult(Quiet([Queued(1)]));
        }, cancel.Token);

        (await FirstAsync(channel.Reader)).Should().BeOfType<SnapshotMsg>().Which.Snapshot.Open.Should().ContainSingle();
        await cancel.CancelAsync();
        reads.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>The reader catches each section's failure itself; a read that still throws must become a line on screen and not the end of the feed.</summary>
    [Fact]
    public async Task AFeedReadThatThrowsPostsASnapshotThatSaysSo()
    {
        var channel = NewChannel();
        using var cancel = new CancellationTokenSource();

        TuiLoop.StartFeed(channel.Writer, _ => throw new InvalidOperationException("The database is gone."), cancel.Token);

        var snapshot = ((SnapshotMsg)await FirstAsync(channel.Reader)).Snapshot;
        await cancel.CancelAsync();
        snapshot.Open.Should().BeNull();
        snapshot.Problems.Should().Equal("Couldn't read the data: The database is gone.");
    }

    [Fact]
    public async Task TheTickerPostsTheClockAndTheWindowSizeAtOnce()
    {
        var channel = NewChannel();
        using var cancel = new CancellationTokenSource();

        TuiLoop.StartTicker(channel.Writer, new ManualTime(Now), () => (100, 31), cancel.Token);

        (await FirstAsync(channel.Reader)).Should().Be(new TickMsg(Now, 100, 31));
        await cancel.CancelAsync();
    }
}
