using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli.Features.Tui;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;
using static VideoOptimiser.UnitTests.Features.Tui.TuiTestData;

namespace VideoOptimiser.UnitTests.Features.Tui;

/// <summary>
/// What the feed reads: the real queue grouping and lock files over canned job lists, a fake worker and an in-memory event
/// file. A read that throws leaves its section null, which is how the model knows to keep what it had.
/// </summary>
public sealed class TuiSnapshotReaderTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly CannedJobs _jobs;
    private readonly FakeWorkerLifecycle _worker = new();
    private readonly MemoryEventLog _events = new();
    private readonly AppSettings _settings = new();
    private readonly DatabaseLocks _locks = new();

    public TuiSnapshotReaderTests()
    {
        Directory.CreateDirectory(_directory);
        _settings.Database.Path = Path.Combine(_directory, "jobs.db");
        _settings.Processing.RemoteSsh.Lifecycle = RemoteLifecycleModes.Hetzner;
        _jobs = new CannedJobs(new SqliteJobRepository(new SqliteDatabaseInitializer()));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private TuiSnapshotReader Reader(IRunEventLog? events = null) => new(
        new QueueDiscovery(null!, _jobs, new FileFingerprintService(), _locks),
        _jobs,
        _locks,
        new SqliteRunRequestStore(new SqliteDatabaseInitializer()),
        _worker,
        events ?? _events);

    private Task<TuiSnapshot> ReadAsync(IRunEventLog? events = null) => Reader(events).ReadAsync(_settings, CancellationToken.None);

    private static JobRecord Job(int number, JobStatus status, int minute, string? path = null, string? category = null, string? message = null) => new()
    {
        Id = Id(number),
        SourcePath = path ?? $@"D:\Videos\clip-{number:000}.mp4",
        Status = status,
        CreatedUtc = T0.AddMinutes(minute),
        UpdatedUtc = T0.AddMinutes(minute + 1),
        CompletedUtc = status is JobStatus.Failed or JobStatus.Completed ? T0.AddMinutes(minute + 1) : null,
        FailureCategory = category,
        FailureMessage = message,
        SourceSizeBytes = 200 * Mib
    };

    // ---- the failed group -------------------------------------------------------------------

    /// <summary>
    /// A failed video is the target of a retry, so it stays only while nothing newer exists for the same original: a retry's new
    /// job, a later conversion or a later drop replaces it. An older job for the original does not.
    /// </summary>
    [Fact]
    public async Task TheFailedGroupHoldsFailuresNothingNewerReplacedNewestFirst()
    {
        _jobs.Terminal =
        [
            Job(1, JobStatus.Failed, minute: 1),
            Job(2, JobStatus.Failed, minute: 5),
            // Retried and converted since: a newer completed job for the same original.
            Job(3, JobStatus.Failed, minute: 3, path: @"D:\Videos\retried.mp4"),
            Job(4, JobStatus.Completed, minute: 4, path: @"d:\videos\RETRIED.mp4"),
            // Retried and waiting again: a newer open job.
            Job(5, JobStatus.Failed, minute: 2, path: @"D:\Videos\waiting.mp4"),
            // An older job for the original doesn't replace its later failure.
            Job(6, JobStatus.Completed, minute: -5, path: @"D:\Videos\older.mp4"),
            Job(7, JobStatus.Failed, minute: 0, path: @"D:\Videos\older.mp4"),
            Job(8, JobStatus.Cancelled, minute: 6)
        ];
        _jobs.Open = [Job(9, JobStatus.Queued, minute: 7, path: @"D:\Videos\waiting.mp4")];

        var snapshot = await ReadAsync();

        snapshot.Failed!.Select(video => video.Id).Should().Equal(Id(2), Id(1), Id(7));
        snapshot.Failed.Should().OnlyContain(video => video.Group == VideoGroup.Failed);
    }

    [Fact]
    public async Task TheFailedGroupHoldsTheNewestTwentyAtMost()
    {
        _jobs.Terminal = [.. Enumerable.Range(1, 25).Select(number => Job(number, JobStatus.Failed, minute: number))];

        var snapshot = await ReadAsync();

        snapshot.Failed!.Should().HaveCount(TuiSnapshotReader.MostFailed);
        snapshot.Failed.First().Id.Should().Be(Id(25));
        snapshot.Failed.Last().Id.Should().Be(Id(6));
    }

    /// <summary>VOPT-115: a dismissed failure leaves the group, and the cap counts only the ones still shown.</summary>
    [Fact]
    public async Task TheFailedGroupLeavesOutDismissedFailures()
    {
        _jobs.Terminal = [.. Enumerable.Range(1, 25).Select(number =>
        {
            var job = Job(number, JobStatus.Failed, minute: number);
            if (number > 20) job.DismissedUtc = T0;
            return job;
        })];

        var snapshot = await ReadAsync();

        snapshot.Failed!.Select(video => video.Id).Should().Equal(Enumerable.Range(1, 20).Reverse().Select(Id));
    }

    /// <summary>The category is a name for code and <c>--json</c>; a person reads a phrase.</summary>
    [Fact]
    public async Task AFailedVideoShowsItsFailureInWordsOnOneLine()
    {
        _jobs.Terminal =
        [
            Job(1, JobStatus.Failed, minute: 1, category: "EncodeFailed", message: "ab-av1 exited with code 1.\nFull log: D:\\logs\\1.log"),
            Job(2, JobStatus.Failed, minute: 2, category: FailureCategories.SourceMissing, message: "Original does not exist: D:\\Videos\\clip-002.mp4")
        ];

        var snapshot = await ReadAsync();

        snapshot.Failed!.Select(video => video.Note).Should().Equal(
            "Original does not exist: D:\\Videos\\clip-002.mp4",
            "encoding failed: ab-av1 exited with code 1.");
    }

    // ---- the queue --------------------------------------------------------------------------

    /// <summary>The groups are <c>queue list</c>'s, in run order, keyed by job ID, with the sizes a ready video's review row needs.</summary>
    [Fact]
    public async Task OpenJobsBecomeVideosInRunOrderWithTheirGroupSizesAndNotes()
    {
        var ready = Job(3, JobStatus.ReadyToFinalize, minute: 3);
        ready.OutputSizeBytes = 80 * Mib;
        ready.PercentageSaved = 60m;
        var failedFinalise = Job(4, JobStatus.ReadyToFinalize, minute: 4, message: "Original is in use.");
        var held = Job(2, JobStatus.Queued, minute: 2);
        held.Held = true;
        var stopped = Job(5, JobStatus.Interrupted, minute: 5);
        stopped.ResumeStatus = JobStatus.Encoding;
        stopped.ExecutionMode = ProcessingModes.RemoteSsh;
        _jobs.Open = [stopped, failedFinalise, ready, held, Job(1, JobStatus.Queued, minute: 1)];

        var snapshot = await ReadAsync();

        snapshot.Open!.Select(video => (video.Id, video.Group, video.Stopped)).Should().Equal(
            (Id(1), VideoGroup.Queue, false),
            (Id(2), VideoGroup.Held, false),
            (Id(3), VideoGroup.Ready, false),
            (Id(4), VideoGroup.Ready, false),
            (Id(5), VideoGroup.Queue, true));
        var readyVideo = snapshot.Open!.Single(video => video.Id == Id(3));
        (readyVideo.Name, readyVideo.SizeBytes, readyVideo.Av1Bytes, readyVideo.PercentSaved, readyVideo.Note).Should().Be(("clip-003.mp4", 200 * Mib, 80 * Mib, 60m, null));
        snapshot.Open!.Single(video => video.Id == Id(4)).Note.Should().Be("last finalising failed");
        snapshot.Open!.Single(video => video.Id == Id(5)).Remote.Should().BeTrue();
    }

    // ---- a read that throws -----------------------------------------------------------------

    [Fact]
    public async Task AQueueReadThatThrowsLeavesTheQueueAndFailedSectionsNullAndSaysWhy()
    {
        _jobs.FailOpen = new IOException("database is locked");

        var snapshot = await ReadAsync();

        snapshot.Open.Should().BeNull();
        snapshot.Failed.Should().BeNull();
        snapshot.Problems.Should().Equal("Couldn't read the queue: database is locked.");
        // Everything else was still read.
        snapshot.Run.Should().NotBeNull();
        snapshot.Worker.Should().NotBeNull();
        snapshot.Events.Should().NotBeNull();
    }

    [Fact]
    public async Task AHistoryReadThatThrowsKeepsTheOpenVideos()
    {
        _jobs.Open = [Job(1, JobStatus.Queued, minute: 1)];
        _jobs.FailTerminal = new IOException("database is locked");

        var snapshot = await ReadAsync();

        snapshot.Open.Should().ContainSingle();
        snapshot.Failed.Should().BeNull();
        snapshot.Problems.Should().Equal("Couldn't read the failed videos: database is locked.");
    }

    [Fact]
    public async Task EachOtherSectionThatThrowsIsNullWithItsOwnProblem()
    {
        _worker.ReadFailure = new IOException("state file is in use");

        var snapshot = await ReadAsync(new ThrowingEventLog());

        snapshot.Worker.Should().BeNull();
        snapshot.Events.Should().BeNull();
        snapshot.Open.Should().NotBeNull();
        snapshot.Problems.Should().Equal("Couldn't read the worker: state file is in use.", "Couldn't read the run's events: the event file is in use.");
    }

    // ---- run, worker, events ----------------------------------------------------------------

    [Fact]
    public async Task ARunHoldingTheLockIsReadWithItsRequestsAndNoRunMeansNoRequests()
    {
        (await ReadAsync()).Run.Should().Be(new RunSection(null, null));

        var requests = new SqliteRunRequestStore(new SqliteDatabaseInitializer());
        await requests.ResetAsync(_settings.Database.Path, WorkerFateChoice.Keep, CancellationToken.None);
        await requests.RequestStopAsync(_settings.Database.Path, now: false, CancellationToken.None);
        using var run = _locks.TakeRun(_settings.Database.Path, LockHolders.BackgroundRun, out _);

        var snapshot = await ReadAsync();

        snapshot.Run!.Holder.Should().Match<LiveRunHolder>(holder => holder.Command == LockHolders.BackgroundRun && holder.ProcessId == Environment.ProcessId);
        snapshot.Run.Requests.Should().Be(new RunRequests(StopAfterThisVideo: true, StopNow: false, WorkerFate: WorkerFateChoice.Keep));
    }

    /// <summary>The state file is read every second, which is cheap; Hetzner is asked on its own schedule, never here.</summary>
    [Fact]
    public async Task TheWorkerSectionReadsTheStateFileForAHetznerLifecycleAndNeverAsksHetzner()
    {
        var snapshot = await ReadAsync();

        snapshot.Worker.Should().Be(new WorkerSection(true, "hetzner", CliFakes.Worker));
        _worker.CheckCalls.Should().Be(0);
    }

    [Fact]
    public async Task AnyOtherLifecycleHasNoManagedWorkerToRead()
    {
        _settings.Processing.RemoteSsh.Lifecycle = RemoteLifecycleModes.Manual;

        var snapshot = await ReadAsync();

        snapshot.Worker.Should().Be(new WorkerSection(false, "manual", null));
    }

    [Fact]
    public async Task TheEventsAreWhateverTheEventFileHolds()
    {
        _events.Append(Started());
        _events.Append(Event(RunEventKind.VideosHeld, count: 1));

        var snapshot = await ReadAsync();

        snapshot.Events!.Select(runEvent => runEvent.Kind).Should().Equal(RunEventKind.RunStarted, RunEventKind.VideosHeld);
    }

    /// <summary>The check calls Hetzner for up to 15 seconds and runs off the loop thread; a failure is a reason to show, never a crash of the TUI.</summary>
    [Fact]
    public async Task AHetznerCheckThatThrowsAnswersWithTheReasonItCouldNotBeAsked()
    {
        _worker.CheckFailure = new InvalidOperationException("The state file is unreadable.\nSecond line.");

        var check = await Reader().CheckWorkerAsync(_settings, CancellationToken.None);

        check.Should().Be(new WorkerCheck(null, null, "The state file is unreadable."));
    }

    [Fact]
    public async Task ACancelledCheckStaysCancelledSoQuittingNeverShowsAReason()
    {
        _worker.CheckFailure = new OperationCanceledException();

        var act = () => Reader().CheckWorkerAsync(_settings, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>The real queue and database path: the settings' database is what every read is asked about.</summary>
    [Fact]
    public async Task EveryReadIsAskedAboutTheConfiguredDatabase()
    {
        await ReadAsync();
        await Reader().CheckWorkerAsync(_settings, CancellationToken.None);

        _jobs.DatabasePaths.Should().OnlyContain(path => path == _settings.Database.Path);
        _worker.DatabasePaths.Should().OnlyContain(path => path == _settings.Database.Path);
    }

    private sealed class ThrowingEventLog : IRunEventLog
    {
        public void Begin(string databasePath) => throw new NotSupportedException();
        public void Append(RunEvent runEvent) => throw new NotSupportedException();
        public IReadOnlyList<RunEvent> Read(string databasePath) => throw new IOException("the event file is in use");
    }

    /// <summary>The job lists a test sets, and a failure to throw instead.</summary>
    private sealed class CannedJobs(IJobRepository inner) : DelegatingJobRepository(inner)
    {
        public IReadOnlyList<JobRecord> Open { get; set; } = [];
        public IReadOnlyList<JobRecord> Terminal { get; set; } = [];
        public Exception? FailOpen { get; set; }
        public Exception? FailTerminal { get; set; }
        public List<string> DatabasePaths { get; } = [];

        public override Task<IReadOnlyList<JobRecord>> ListAsync(string databasePath, bool terminal, CancellationToken cancellationToken = default)
        {
            DatabasePaths.Add(databasePath);
            var failure = terminal ? FailTerminal : FailOpen;
            return failure is null ? Task.FromResult(terminal ? Terminal : Open) : Task.FromException<IReadOnlyList<JobRecord>>(failure);
        }
    }
}
