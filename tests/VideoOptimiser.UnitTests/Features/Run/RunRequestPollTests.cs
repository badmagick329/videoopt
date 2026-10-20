using FluentAssertions;
using VideoOptimiser.Application.Features.Run;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// How the requests row drives <see cref="RunStop"/>. The row itself is the database's (<c>SqliteRunRequestStore</c>), and the
/// run's reaction is <see cref="BackgroundRunTests"/>'; this is only that each request is passed on once, that a cleared stop
/// after this video is withdrawn, and that the poll survives a busy database.
/// </summary>
public sealed class RunRequestPollTests
{
    private static readonly RunRequests Nothing = new(false, false, null);
    private static readonly RunRequests AfterThisVideo = new(true, false, null);
    private static readonly RunRequests Now = new(true, true, null);
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// The row keeps saying "stop after this video" on every read until a withdraw or the next run clears it. A second Press
    /// would be a second Ctrl+C and stop the video, so only the first read that shows it counts, and likewise for "now".
    /// </summary>
    [Fact]
    public async Task EachRequestIsPassedOnOnceHoweverOftenTheRowShowsIt()
    {
        using var stop = new RunStop();
        var stoppingAfterThisVideo = 0;
        using var converting = stop.WhileConverting(() => stoppingAfterThisVideo++);
        var script = new[] { Nothing, AfterThisVideo, AfterThisVideo, AfterThisVideo, Now };
        var nowCancelledAtRead = new List<bool>();
        var requests = new ScriptedRequests(read =>
        {
            nowCancelledAtRead.Add(stop.Now.IsCancellationRequested);
            return script[read];
        });

        await new RunRequestPoll(requests, Fast).PollAsync("jobs.db", stop, CancellationToken.None);

        stoppingAfterThisVideo.Should().Be(1);
        stop.StopsAfterThisVideo.Should().BeTrue();
        nowCancelledAtRead.Should().Equal(false, false, false, false, false);
        stop.Now.IsCancellationRequested.Should().BeTrue();
        requests.Reads.Should().Be(5, "the poll ends by itself once both requests are passed on");
    }

    /// <summary>
    /// VOPT-187: the poll follows the row's stop after this video both ways. Cleared, it is withdrawn, so the run carries on; set
    /// again, it is a first press again, not a second one that stops now; and the poll keeps reading after a withdraw, so a stop
    /// now still reaches the run.
    /// </summary>
    [Fact]
    public async Task AClearedStopAfterThisVideoIsWithdrawnAndASetOneIsPressedAgain()
    {
        using var stop = new RunStop();
        var stoppingAfterThisVideo = 0;
        using var converting = stop.WhileConverting(() => stoppingAfterThisVideo++);
        var script = new[] { Nothing, AfterThisVideo, AfterThisVideo, Nothing, Nothing, AfterThisVideo, Now };
        var stoppingAtRead = new List<bool>();
        var requests = new ScriptedRequests(read =>
        {
            stoppingAtRead.Add(stop.StopsAfterThisVideo);
            return script[read];
        });

        await new RunRequestPoll(requests, Fast).PollAsync("jobs.db", stop, CancellationToken.None);

        stoppingAtRead.Should().Equal(false, false, true, true, false, false, true);
        stoppingAfterThisVideo.Should().Be(2);
        stop.Now.IsCancellationRequested.Should().BeTrue();
        requests.Reads.Should().Be(7);
    }

    /// <summary>A busy database fails a read; the request is still in the row, so the next read finds it.</summary>
    [Fact]
    public async Task AFailedReadIsSkipped()
    {
        using var stop = new RunStop();
        using var converting = stop.WhileConverting(() => { });
        var requests = new ScriptedRequests(read => read < 2 ? throw new InvalidOperationException("database is locked") : Now);

        await new RunRequestPoll(requests, Fast).PollAsync("jobs.db", stop, CancellationToken.None);

        stop.Now.IsCancellationRequested.Should().BeTrue();
        requests.Reads.Should().Be(3);
    }

    [Fact]
    public async Task ThePollEndsQuietlyWhenTheRunDoes()
    {
        using var stop = new RunStop();
        using var end = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var poll = new RunRequestPoll(new ScriptedRequests(_ => Nothing), Fast).PollAsync("jobs.db", stop, end.Token);

        await poll.WaitAsync(TimeSpan.FromSeconds(10));
        stop.StopsAfterThisVideo.Should().BeFalse();
        stop.Now.IsCancellationRequested.Should().BeFalse();
    }

    private sealed class ScriptedRequests(Func<int, RunRequests> read) : IRunRequestStore
    {
        public int Reads { get; private set; }

        public Task<RunRequests> ReadAsync(string databasePath, CancellationToken cancellationToken) => Task.FromResult(read(Reads++));

        public Task ResetAsync(string databasePath, WorkerFateChoice? fate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RequestStopAsync(string databasePath, bool now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> WithdrawStopAsync(string databasePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetWorkerFateAsync(string databasePath, WorkerFateChoice fate, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
