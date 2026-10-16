using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// The run's flow with no terminal: a recording observer and a scripted questions port stand in for the CLI, so a front end
/// that prints nothing (a background runner) or draws its own screen (a TUI) can drive the same flow. What the CLI prints
/// from these events is covered through the real CLI (<see cref="WorkerEndRunTests"/>).
/// </summary>
public sealed class RunFlowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeWorkerLifecycle _lifecycle = new();
    private readonly RecordingRunObserver _observer = new();

    public RunFlowTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    /// <summary>
    /// A stop now while a video converts on this PC, after the user answered y during the run: nothing on the worker is lost,
    /// so the worker is deleted without asking again, and the stop still exits 9.
    /// </summary>
    [Fact]
    public async Task AStopNowAfterAYesDeletesTheWorkerWithoutAskingAgain()
    {
        var jobs = await RunCli.QueueJobsAsync(_repository, DatabasePath, 1);
        using var stopNow = new CancellationTokenSource();
        var processor = new ScriptedRunProcessor(remote: false);
        processor.During[0] = _ => { stopNow.Cancel(); return Task.CompletedTask; };
        var questions = new ScriptedQuestions(new RunWorkerAnswer(true, Pending: false));

        var code = await RunAsync(processor, questions, stopNow.Token);

        code.Should().Be((int)ExitCode.Cancelled);
        _lifecycle.DeleteCalls.Should().Be(1);
        questions.Asked.Should().BeFalse();
        _observer.Events.Should().ContainInOrder("ConversionStarted:1,workerUp=True", "ConversionEnded", "StoppedNow:Delete", "WorkerDeleting", "WorkerDeleteEnded");
        _observer.WorkerLines.Should().Contain("Worker deleted; billing for it and its IPv4 address has stopped.");
        (await RunCli.StatusesAsync(_repository, DatabasePath, jobs)).Should().Equal(JobStatus.Interrupted);
    }

    /// <summary>
    /// An N given during the run keeps the worker even though a finished run that used it would delete it: the user's answer
    /// outranks the setting.
    /// </summary>
    [Fact]
    public async Task AFinishedRunAfterANoKeepsTheWorkerAndDeletesNothing()
    {
        var jobs = await RunCli.QueueJobsAsync(_repository, DatabasePath, 1);
        var processor = new ScriptedRunProcessor(remote: true);
        var questions = new ScriptedQuestions(new RunWorkerAnswer(false, Pending: false));

        var code = await RunAsync(processor, questions, CancellationToken.None);

        code.Should().Be((int)ExitCode.Success);
        _lifecycle.EnsureCalls.Should().Be(1);
        _lifecycle.DeleteCalls.Should().Be(0);
        _observer.Events.Should().Contain("WorkerKept:Finished").And.NotContain("WorkerDeleting");
        _observer.Events.Should().Contain("Summary:ready=1,total=1,stoppedAfter=False");
        (await RunCli.StatusesAsync(_repository, DatabasePath, jobs)).Should().Equal(JobStatus.ReadyToFinalize);
    }

    /// <summary>The scenarios of <see cref="TheWorkerFateChosenAtStartIsAppliedOnEveryWayABackgroundRunEnds"/>: how the run ends.</summary>
    public enum BackgroundEnd
    {
        Finished,
        StoppedAfterVideo,
        StoppedNowWithWorkOnTheWorker,
        StoppedNowWithoutWork,
        WorkerUnreachable,
        Failed
    }

    /// <summary>
    /// D3's done criterion (VOPT-110): a background run asks nobody, so the fate chosen at start is the answer, and it decides at
    /// every end. Delete deletes even when a stop now throws away work only the worker has (nobody can be asked again), and
    /// Keep keeps whatever the settings say about a finished run. The unreachable-worker end deletes silently under Delete, as a
    /// y does; under Keep it leaves the worker with the unreachable lines.
    /// </summary>
    [Theory]
    [InlineData(BackgroundEnd.Finished, WorkerFateChoice.Delete)]
    [InlineData(BackgroundEnd.Finished, WorkerFateChoice.Keep)]
    [InlineData(BackgroundEnd.StoppedAfterVideo, WorkerFateChoice.Delete)]
    [InlineData(BackgroundEnd.StoppedAfterVideo, WorkerFateChoice.Keep)]
    [InlineData(BackgroundEnd.StoppedNowWithWorkOnTheWorker, WorkerFateChoice.Delete)]
    [InlineData(BackgroundEnd.StoppedNowWithWorkOnTheWorker, WorkerFateChoice.Keep)]
    [InlineData(BackgroundEnd.StoppedNowWithoutWork, WorkerFateChoice.Delete)]
    [InlineData(BackgroundEnd.StoppedNowWithoutWork, WorkerFateChoice.Keep)]
    [InlineData(BackgroundEnd.WorkerUnreachable, WorkerFateChoice.Delete)]
    [InlineData(BackgroundEnd.WorkerUnreachable, WorkerFateChoice.Keep)]
    [InlineData(BackgroundEnd.Failed, WorkerFateChoice.Delete)]
    [InlineData(BackgroundEnd.Failed, WorkerFateChoice.Keep)]
    public async Task TheWorkerFateChosenAtStartIsAppliedOnEveryWayABackgroundRunEnds(BackgroundEnd end, WorkerFateChoice fate)
    {
        await RunCli.QueueJobsAsync(_repository, DatabasePath, 2);
        using var stopNow = new CancellationTokenSource();
        using var stopAfter = new CancellationTokenSource();
        var processor = new ScriptedRunProcessor(remote: end != BackgroundEnd.StoppedNowWithoutWork);
        switch (end)
        {
            case BackgroundEnd.StoppedAfterVideo:
                processor.During[0] = _ => { stopAfter.Cancel(); return Task.CompletedTask; };
                break;
            case BackgroundEnd.StoppedNowWithWorkOnTheWorker or BackgroundEnd.StoppedNowWithoutWork:
                processor.During[0] = _ => { stopNow.Cancel(); return Task.CompletedTask; };
                break;
            case BackgroundEnd.WorkerUnreachable:
                processor.Unreachable[0] = true;
                break;
            case BackgroundEnd.Failed:
                processor.Throw[0] = new InvalidOperationException("the disk broke");
                break;
        }
        var requests = new SqliteRunRequestStore(new SqliteDatabaseInitializer());
        await requests.ResetAsync(DatabasePath, fate, CancellationToken.None);

        _ = await RunAsync(processor, new BackgroundRunQuestions(requests, DatabasePath, fate), stopNow.Token, stopAfter.Token);

        _lifecycle.DeleteCalls.Should().Be(fate == WorkerFateChoice.Delete ? 1 : 0);
        // The end it was meant to reach, and what the worker's fate looked like there.
        var (reached, kept) = end switch
        {
            BackgroundEnd.Finished => ("Summary:ready=2,total=2,stoppedAfter=False", "WorkerKept:Finished"),
            BackgroundEnd.StoppedAfterVideo => ("StoppedAfter", "WorkerKept:StoppedAfterVideo"),
            BackgroundEnd.StoppedNowWithWorkOnTheWorker => ("StoppedNow:" + fate, null),
            BackgroundEnd.StoppedNowWithoutWork => ("StoppedNow:" + fate, null),
            BackgroundEnd.WorkerUnreachable => ("StoppedByUnreachableWorker", "WorkerUnreachableAtEnd"),
            _ => ("RunFailed", "WorkerKept:Failed")
        };
        _observer.Events.Should().Contain(reached);
        if (fate == WorkerFateChoice.Delete) _observer.Events.Should().Contain("WorkerDeleting").And.NotContain("WorkerKept:" + end);
        else if (kept is not null) _observer.Events.Should().Contain(kept).And.NotContain("WorkerDeleting");
        else _observer.Events.Should().NotContain("WorkerDeleting");
    }

    /// <summary>
    /// The fate is read when the run ends, not when it starts, so a change made while a video converts decides: that is what
    /// lets a person keep the worker after starting the run to delete it, or the reverse.
    /// </summary>
    [Theory]
    [InlineData(WorkerFateChoice.Delete, WorkerFateChoice.Keep)]
    [InlineData(WorkerFateChoice.Keep, WorkerFateChoice.Delete)]
    public async Task AFateChangedWhileTheRunGoesIsTheOneAppliedAtTheEnd(WorkerFateChoice atStart, WorkerFateChoice changedTo)
    {
        await RunCli.QueueJobsAsync(_repository, DatabasePath, 1);
        var requests = new SqliteRunRequestStore(new SqliteDatabaseInitializer());
        await requests.ResetAsync(DatabasePath, atStart, CancellationToken.None);
        var processor = new ScriptedRunProcessor(remote: true);
        processor.During[0] = _ => requests.SetWorkerFateAsync(DatabasePath, changedTo, CancellationToken.None);

        _ = await RunAsync(processor, new BackgroundRunQuestions(requests, DatabasePath, atStart), CancellationToken.None);

        _lifecycle.DeleteCalls.Should().Be(changedTo == WorkerFateChoice.Delete ? 1 : 0);
    }

    /// <summary>
    /// A busy or locked database as the run ends must not leave the worker's fate undecided: with nobody watching a background
    /// run, an undecided worker bills on unnoticed. The fate chosen at launch decides instead.
    /// </summary>
    [Theory]
    [InlineData(WorkerFateChoice.Delete)]
    [InlineData(WorkerFateChoice.Keep)]
    public async Task AnUnreadableRequestsRowAtTheEndAppliesTheFateChosenAtLaunch(WorkerFateChoice atStart)
    {
        await RunCli.QueueJobsAsync(_repository, DatabasePath, 1);
        var processor = new ScriptedRunProcessor(remote: true);

        _ = await RunAsync(processor, new BackgroundRunQuestions(new UnreadableRequests(), DatabasePath, atStart), CancellationToken.None);

        _lifecycle.DeleteCalls.Should().Be(atStart == WorkerFateChoice.Delete ? 1 : 0);
        _observer.Events.Should().NotContain(e => e.StartsWith("RunFailed", StringComparison.Ordinal));
    }

    private sealed class UnreadableRequests : IRunRequestStore
    {
        public Task ResetAsync(string databasePath, WorkerFateChoice? fate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<RunRequests> ReadAsync(string databasePath, CancellationToken cancellationToken) => throw new IOException("database is locked");
        public Task RequestStopAsync(string databasePath, bool now, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetWorkerFateAsync(string databasePath, WorkerFateChoice fate, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private async Task<int> RunAsync(ScriptedRunProcessor processor, IRunQuestions questions, CancellationToken stopNow, CancellationToken stopAfterThisVideo = default)
    {
        var settings = Settings();
        var locks = new DatabaseLocks();
        var flow = new RunFlow(new QueueService(_repository, processor, locks, new CommandHints(null)), new QueueDiscovery(null!, _repository, new FileFingerprintService(), locks), processor, _lifecycle, new NoImmichRestaging());
        flow.MarkStarted();
        // The same shape as the CLI's: the guard decides the worker's fate for an end the conversion does not decide itself.
        return await flow.GuardAsync(settings, _observer, questions, async () => (int)await flow.ConvertQueueAsync(settings, modeOverridden: false, _observer, questions, stopAfterThisVideo, stopNow));
    }

    private AppSettings Settings() => new()
    {
        Database = new DatabaseSettings { Path = DatabasePath },
        Processing = new ProcessingSettings { Mode = ProcessingModes.RemoteSsh, RemoteSsh = new RemoteSshSettings { Lifecycle = RemoteLifecycleModes.Hetzner } }
    };

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class ScriptedQuestions(RunWorkerAnswer answer) : IRunQuestions
    {
        public bool Asked { get; private set; }
        public bool CanAsk => false;
        public Task<bool> ConfirmWorkerCreationAsync(RemoteWorkerCreationQuote quote, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<RunWorkerAnswer> AnswerGivenDuringRunAsync() => Task.FromResult(answer);

        public Task<bool> AskToDeleteWorkerAsync(RunEnd runEnd, JobStatus? remoteStage)
        {
            Asked = true;
            return Task.FromResult(false);
        }
    }
}
