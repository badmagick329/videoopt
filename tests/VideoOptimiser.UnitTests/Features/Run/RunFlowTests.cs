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
    private readonly RecordingObserver _observer = new();

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

    private async Task<int> RunAsync(ScriptedRunProcessor processor, ScriptedQuestions questions, CancellationToken stopNow)
    {
        var settings = Settings();
        var locks = new DatabaseLocks();
        var flow = new RunFlow(new QueueService(_repository, processor, locks, new CommandHints(null)), new QueueDiscovery(null!, _repository, new FileFingerprintService(), locks), processor, _lifecycle, new UnusedImmich());
        flow.MarkStarted();
        // The same shape as the CLI's: the guard decides the worker's fate for an end the conversion does not decide itself.
        return await flow.GuardAsync(settings, _observer, questions, async () => (int)await flow.ConvertQueueAsync(settings, modeOverridden: false, _observer, questions, stopAfterThisVideo: CancellationToken.None, stopNow));
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
        public RunWorkerAnswer AnswerGivenDuringRun() => answer;

        public Task<bool> AskToDeleteWorkerAsync(RunEnd runEnd, JobStatus? remoteStage)
        {
            Asked = true;
            return Task.FromResult(false);
        }
    }

    private sealed class UnusedImmich : IImmichRestaging
    {
        public Task<ImmichRestageResult> RestageMissingAsync(string databasePath, AppSettings settings, IReadOnlyCollection<JobRecord> jobs, IProgress<ImmichProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No Immich video is queued.");
    }

    /// <summary>Records each event as a short name, in the order the flow raised it.</summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<string> Events { get; } = [];
        public List<string> WorkerLines { get; } = [];

        public void VideoSkipped(JobRecord job) => Events.Add("VideoSkipped");
        public void FinalisationInterrupted(JobRecord job) => Events.Add("FinalisationInterrupted");
        public void VideosHeld(int count) => Events.Add($"VideosHeld:{count}");
        public IProgress<ImmichProgress> ImmichRestaging() => new SynchronousProgress<ImmichProgress>(_ => { });
        public void ImmichVideosStayQueued(int count, string? unusableReason) => Events.Add($"ImmichVideosStayQueued:{count}");
        public void WorkerProgress(string line) => WorkerLines.Add(line);

        public IProgress<JobProgress> ConversionStarted(int count, bool workerUp)
        {
            Events.Add($"ConversionStarted:{count},workerUp={workerUp}");
            return new SynchronousProgress<JobProgress>(_ => { });
        }

        public void ConversionEnded() => Events.Add("ConversionEnded");
        public void JoiningVideoSkipped(JobRecord job) => Events.Add("JoiningVideoSkipped");
        public void JoiningVideoWaitsForWorker(JobRecord job) => Events.Add("JoiningVideoWaitsForWorker");
        public void StoppedByUnreachableWorker(string workerName, int notConverted) => Events.Add("StoppedByUnreachableWorker");
        public void StoppedAfter(JobRecord last, int notStarted) => Events.Add("StoppedAfter");
        public void Summary(int ready, int skipped, IReadOnlyList<FailedQueueJob> failures, int readyTotal, bool stoppedAfter) => Events.Add($"Summary:ready={ready},total={readyTotal},stoppedAfter={stoppedAfter}");
        public void StoppedWithoutManagedWorker(JobInterruptedException? interrupted, string? manualHost) => Events.Add("StoppedWithoutManagedWorker");
        public void StoppedNow(JobInterruptedException? interrupted, string worker, WorkerFate fate, bool settingUpWorker) => Events.Add($"StoppedNow:{fate}");
        public void WorkerUnreachableAtEnd(JobRecord job, bool questionFollows) => Events.Add("WorkerUnreachableAtEnd");
        public void WorkerKept(string worker, RunEnd runEnd, JobStatus? remoteStage, bool answeredNo) => Events.Add($"WorkerKept:{runEnd}");
        public void WorkerDeleting() => Events.Add("WorkerDeleting");
        public void WorkerDeleteFailed(string worker, string message) => Events.Add("WorkerDeleteFailed");
        public void WorkerDeleteEnded() => Events.Add("WorkerDeleteEnded");
        public void ManualWorkerUnreachable(JobRecord job) => Events.Add("ManualWorkerUnreachable");
        public void ManualWorkerReminder() => Events.Add("ManualWorkerReminder");

        public int RunFailed(Exception exception)
        {
            Events.Add("RunFailed");
            return (int)ExitCode.GeneralFailure;
        }
    }
}
