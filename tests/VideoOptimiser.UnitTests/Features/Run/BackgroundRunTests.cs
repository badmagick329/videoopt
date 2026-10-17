using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Features.Run;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// A background run end to end below the CLI: the real lock, requests row and event file against a temporary database, a fake
/// worker, and a processor scripted per video. What the worker's fate does at each end is <see cref="RunFlowTests"/>'s; this is
/// how the run starts, how a stop request reaches it, and what it leaves behind.
/// </summary>
public sealed class BackgroundRunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly WatchedRequests _requests = new(new SqliteRunRequestStore(new SqliteDatabaseInitializer()));
    private readonly SqliteRunRecordStore _records = new(new SqliteDatabaseInitializer());
    private readonly FakeWorkerLifecycle _lifecycle = new();
    private readonly FakeKeepAwake _keepAwake = new();
    private readonly RunEventFile _events = new();
    private readonly RunStop _stop = new();

    public BackgroundRunTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    /// <summary>The first stop request while a video converts is the first Ctrl+C: the video finishes, the rest wait, and nothing is cancelled.</summary>
    [Fact]
    public async Task AStopRequestWhileAVideoConvertsStopsAfterThatVideo()
    {
        var jobs = await RunCli.QueueJobsAsync(_repository, DatabasePath, 3);
        var processor = new ScriptedRunProcessor();
        processor.During[0] = async _ =>
        {
            await _requests.RequestStopAsync(DatabasePath, now: false, CancellationToken.None);
            await WaitUntilAsync(() => _stop.AfterThisVideo.IsCancellationRequested);
        };

        var code = await RunAsync(processor);

        code.Should().Be((int)ExitCode.Cancelled);
        processor.Processed.Should().Equal("1.mp4");
        (await RunCli.StatusesAsync(_repository, DatabasePath, jobs)).Should().Equal(JobStatus.ReadyToFinalize, JobStatus.Queued, JobStatus.Queued);
        _stop.Now.IsCancellationRequested.Should().BeFalse();
        Kinds().Should().ContainInOrder(RunEventKind.RunStarted, RunEventKind.ConversionStarted, RunEventKind.ConversionEnded, RunEventKind.StoppedAfter, RunEventKind.Summary);
    }

    /// <summary>The first Ctrl+C with nothing converting stops now, so the first stop request does too; here the run is still creating its worker.</summary>
    [Fact]
    public async Task AStopRequestWithNoVideoConvertingStopsNowAndAppliesTheFate()
    {
        await RunCli.QueueJobsAsync(_repository, DatabasePath, 2);
        var processor = new ScriptedRunProcessor(remote: true);
        _lifecycle.DuringSetup = async token =>
        {
            await _requests.RequestStopAsync(DatabasePath, now: false, CancellationToken.None);
            await Task.Delay(Timeout.Infinite, token);
        };

        var code = await RunAsync(processor, remote: true);

        code.Should().Be((int)ExitCode.Cancelled);
        processor.Calls.Should().Be(0);
        _lifecycle.DeleteCalls.Should().Be(1);
        _events.Read(DatabasePath).Should().Contain(entry => entry.Kind == RunEventKind.StoppedNow && entry.Flag == true && entry.Fate == "Delete");
    }

    /// <summary>A stop now is the second Ctrl+C: the video is interrupted where it is and resumes from that stage on the next run.</summary>
    [Fact]
    public async Task AStopNowRequestInterruptsTheVideoAtOnce()
    {
        var jobs = await RunCli.QueueJobsAsync(_repository, DatabasePath, 3);
        var processor = new ScriptedRunProcessor();
        processor.During[0] = async _ =>
        {
            await _requests.RequestStopAsync(DatabasePath, now: true, CancellationToken.None);
            await WaitUntilAsync(() => _stop.Now.IsCancellationRequested);
        };

        var code = await RunAsync(processor);

        code.Should().Be((int)ExitCode.Cancelled);
        var stored = await _repository.GetAsync(DatabasePath, jobs[0].Id);
        (stored!.Status, stored.ResumeStatus).Should().Be((JobStatus.Interrupted, JobStatus.Encoding));
        (await RunCli.StatusesAsync(_repository, DatabasePath, jobs)).Skip(1).Should().OnlyContain(status => status == JobStatus.Queued);
        _events.Read(DatabasePath).Should().Contain(entry => entry.Kind == RunEventKind.StoppedWithoutManagedWorker && entry.Stage == "Encoding" && entry.JobId == jobs[0].Id);
    }

    /// <summary>
    /// Deleting the worker is not interrupted by any stop request, or the worker would be left billing. The delete finishes, the
    /// run ends as finished, and its stop-now token stays uncancelled.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStopRequestDuringTheWorkerDeleteDoesNotCutItOff(bool now)
    {
        await RunCli.QueueJobsAsync(_repository, DatabasePath, 1);
        _lifecycle.DuringDelete = () =>
        {
            _requests.RequestStopAsync(DatabasePath, now, CancellationToken.None).GetAwaiter().GetResult();
            // Two reads after the request: the poll has seen it and done what it does with it.
            var seen = _requests.Reads;
            SpinUntil(() => _requests.Reads >= seen + 2);
        };

        var code = await RunAsync(new ScriptedRunProcessor(remote: true), remote: true);

        code.Should().Be((int)ExitCode.Success);
        _lifecycle.Exists.Should().BeFalse();
        _stop.Now.IsCancellationRequested.Should().BeFalse();
    }

    /// <summary>
    /// A run holds the lock as a background run and keeps Windows awake while it converts; the requests an earlier run left are
    /// gone and its worker fate is recorded; and the lock and the hold are both released when it ends.
    /// </summary>
    [Fact]
    public async Task ARunHoldsTheLockAndKeepsWindowsAwakeOnlyWhileItWorks()
    {
        await RunCli.QueueJobsAsync(_repository, DatabasePath, 1);
        await _requests.RequestStopAsync(DatabasePath, now: true, CancellationToken.None);
        LiveRunHolder? holder = null;
        var awakeWhileConverting = false;
        RunRequests? requestsWhileConverting = null;
        var processor = new ScriptedRunProcessor();
        processor.During[0] = async _ =>
        {
            holder = new DatabaseLocks().LiveRun(DatabasePath);
            awakeWhileConverting = _keepAwake.Held;
            requestsWhileConverting = await _requests.ReadAsync(DatabasePath, CancellationToken.None);
        };

        var code = await RunAsync(processor, WorkerFateChoice.Keep);

        code.Should().Be((int)ExitCode.Success);
        holder.Should().BeEquivalentTo(new { Command = LockHolders.BackgroundRun, ProcessId = Environment.ProcessId });
        awakeWhileConverting.Should().BeTrue();
        requestsWhileConverting.Should().Be(new RunRequests(StopAfterThisVideo: false, StopNow: false, WorkerFateChoice.Keep));
        _keepAwake.Log.Should().Equal("hold", "release");
        new DatabaseLocks().LiveRun(DatabasePath).Should().BeNull();
        var started = _events.Read(DatabasePath)[0];
        (started.Kind, started.Text, started.Count, started.Fate).Should().Be((RunEventKind.RunStarted, LockHolders.BackgroundRun, Environment.ProcessId, "Keep"));
    }

    /// <summary>
    /// A run leaves its row with its start and end, and one closed conversion span per video against it. Both name the worker
    /// only when the run used a managed one: the cost report counts managed workers alone.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARunRecordsItselfAndASpanPerVideoNamingTheWorkerOnlyWhenManaged(bool remote)
    {
        var jobs = await RunCli.QueueJobsAsync(_repository, DatabasePath, 2);

        var code = await RunAsync(new ScriptedRunProcessor(remote), remote: remote);

        code.Should().Be((int)ExitCode.Success);
        long? worker = remote ? CliFakes.Worker.ServerId : null;
        var run = (await RecordRows.QueryAsync(DatabasePath, "SELECT id, holder_name, worker_server_id, started_utc <= ended_utc FROM runs;")).Should().ContainSingle().Subject;
        (run[1], run[2], run[3]).Should().Be(((object?)LockHolders.BackgroundRun, (object?)worker, (object?)1L));
        var spans = await RecordRows.QueryAsync(DatabasePath, "SELECT job_id, run_id, worker_server_id, started_utc <= ended_utc FROM conversion_spans ORDER BY id;");
        spans.Select(span => (span[0], span[1], span[2], span[3])).Should().Equal(jobs.Select(job => ((object?)job.Id.ToString("N"), run[0], (object?)worker, (object?)1L)));
    }

    /// <summary>A run refused by the lock leaves the running one's requests and event file untouched: it must not clear a stop request meant for the run that holds the lock.</summary>
    [Fact]
    public async Task ARunTheLockRefusesTouchesNothing()
    {
        await RunCli.QueueJobsAsync(_repository, DatabasePath, 1);
        using var other = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), LockHolders.BackgroundRun, out _);
        await _requests.RequestStopAsync(DatabasePath, now: true, CancellationToken.None);
        var processor = new ScriptedRunProcessor();

        var code = await RunAsync(processor);

        code.Should().Be((int)ExitCode.GeneralFailure);
        processor.Calls.Should().Be(0);
        _keepAwake.Log.Should().BeEmpty();
        File.Exists(RunEventFile.PathFor(DatabasePath)).Should().BeFalse();
        (await _requests.ReadAsync(DatabasePath, CancellationToken.None)).StopNow.Should().BeTrue();
    }

    /// <summary>A step after the lock fails: the lock is not left held by a run that never began, and the error is in the event file.</summary>
    [Fact]
    public async Task AFailureRightAfterTakingTheLockReleasesItAndIsRecorded()
    {
        await RunCli.QueueJobsAsync(_repository, DatabasePath, 1);
        _requests.ResetFailure = new InvalidOperationException("database is locked");
        var processor = new ScriptedRunProcessor();

        var code = await RunAsync(processor);

        code.Should().Be((int)ExitCode.GeneralFailure);
        processor.Calls.Should().Be(0);
        new DatabaseLocks().LiveRun(DatabasePath).Should().BeNull();
        _keepAwake.Log.Should().BeEmpty();
        _events.Read(DatabasePath).Should().Contain(entry => entry.Kind == RunEventKind.RunFailed && entry.Text == "database is locked");
    }

    private Task<int> RunAsync(ScriptedRunProcessor processor, WorkerFateChoice fate = WorkerFateChoice.Delete, bool remote = false)
    {
        var settings = new AppSettings { Database = new DatabaseSettings { Path = DatabasePath } };
        if (remote) settings.Processing = new ProcessingSettings { Mode = ProcessingModes.RemoteSsh, RemoteSsh = new RemoteSshSettings { Lifecycle = RemoteLifecycleModes.Hetzner } };
        var locks = new DatabaseLocks();
        var flow = new RunFlow(new QueueService(_repository, processor, locks, new CommandHints(null)), new QueueDiscovery(null!, _repository, new FileFingerprintService(), locks), processor, _lifecycle, new NoImmichRestaging(), new FailedValidationRecheck(_repository, new OutputManifestStore(), new FileFingerprintService(), new PassingValidator()), _records, TimeProvider.System);
        var start = new RunStart(locks, _requests, _events, _keepAwake, _records, flow, TimeProvider.System);
        var background = new BackgroundRun(flow, start, new RunRequestPoll(_requests, TimeSpan.FromMilliseconds(10)), _stop, _requests, new EventLogRunObserver(_events, TimeProvider.System));
        return background.RunAsync(settings, modeOverridden: false, fate);
    }

    private IEnumerable<RunEventKind> Kinds() => _events.Read(DatabasePath).Select(entry => entry.Kind);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The run never got there.");
            await Task.Delay(5);
        }
    }

    private static void SpinUntil(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10))) throw new TimeoutException("The run never got there.");
    }

    public void Dispose()
    {
        _events.Dispose();
        _stop.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>The real requests row, counting reads (the poll's) and failing a reset on demand.</summary>
    private sealed class WatchedRequests(IRunRequestStore inner) : IRunRequestStore
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);
        public Exception? ResetFailure { get; set; }

        public Task ResetAsync(string databasePath, WorkerFateChoice? fate, CancellationToken cancellationToken) =>
            ResetFailure is null ? inner.ResetAsync(databasePath, fate, cancellationToken) : Task.FromException(ResetFailure);

        public Task<RunRequests> ReadAsync(string databasePath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return inner.ReadAsync(databasePath, cancellationToken);
        }

        public Task RequestStopAsync(string databasePath, bool now, CancellationToken cancellationToken) => inner.RequestStopAsync(databasePath, now, cancellationToken);

        public Task SetWorkerFateAsync(string databasePath, WorkerFateChoice fate, CancellationToken cancellationToken) => inner.SetWorkerFateAsync(databasePath, fate, cancellationToken);
    }
}
