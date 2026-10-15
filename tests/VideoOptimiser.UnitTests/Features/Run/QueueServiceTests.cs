using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests.Features.Run;

public sealed class QueueServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public QueueServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task RunAsyncCountsASkippedJobAsSkippedNotFailedAndExitsSuccessfully()
    {
        var (databasePath, jobs) = await QueuedAsync("a.mp4");
        var skipped = new JobRecord { Id = jobs[0].Id, SourcePath = jobs[0].SourcePath, Status = JobStatus.Skipped, FailureCategory = "NotWorthConverting", FailureMessage = "AV1 would keep 95%." };
        var queue = new QueueService(_repository, new ResultProcessor(new JobProcessingResult(skipped, ExitCode.Success, "AV1 would keep 95%.")), new DatabaseLocks());

        var result = await queue.RunAsync(databasePath, new AppSettings(), Prepared(jobs));

        result.ReadyToFinalize.Should().Be(0);
        result.FailedJobs.Should().BeEmpty();
        result.SkippedJobs.Should().ContainSingle().Which.Should().Be(new SkippedQueueJob(skipped.Id, skipped.SourcePath, "AV1 would keep 95%."));
        result.ExitCode.Should().Be(ExitCode.Success);
    }

    /// <summary>
    /// Every later job would retry the dead worker and fail the same way, so the run stops at the first unreachable result.
    /// That job resumes, so it is not a failure, but the exit code counts it as one, as before (VOPT-69).
    /// </summary>
    [Fact]
    public async Task RunAsyncStopsAtTheFirstJobWhoseWorkerCannotBeReached()
    {
        var (databasePath, jobs) = await QueuedAsync("1.mp4", "2.mp4", "3.mp4");
        var processor = new UnreachableAfterProcessor(readyBefore: 1);
        var queue = new QueueService(_repository, processor, new DatabaseLocks());

        var result = await queue.RunAsync(databasePath, new AppSettings(), Prepared(jobs));

        processor.Calls.Should().Be(2);
        result.UnreachableWorkerJob!.SourcePath.Should().Be(jobs[1].SourcePath);
        result.NotStarted.Should().Be(1);
        result.FailedJobs.Should().BeEmpty();
        result.ReadyToFinalize.Should().Be(1);
        result.ExitCode.Should().Be(ExitCode.PartialSuccess);
    }

    /// <summary>
    /// The user's choice (VOPT-54 step 5): a run takes each video as it starts it, so a hold, a <c>queue next</c> and a video
    /// queued from another terminal while one converts all count at the next video.
    /// </summary>
    [Fact]
    public async Task RunAsyncTakesTheQueueAsItIsBeforeEachVideo()
    {
        var (databasePath, jobs) = await QueuedAsync("first.mp4", "held.mp4", "later.mp4", "moved-next.mp4");
        var processor = new ScriptedProcessor();
        processor.During["first.mp4"] = async () =>
        {
            (await _repository.SetHeldAsync(databasePath, (await _repository.GetAsync(databasePath, jobs[1].Id))!, held: true)).Should().BeTrue();
            (await _repository.RunNextAsync(databasePath, (await _repository.GetAsync(databasePath, jobs[3].Id))!)).Should().BeTrue();
            await _repository.CreateAsync(databasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "added.mp4"), SourceFingerprint = "added", Status = JobStatus.Queued });
        };
        var queue = new QueueService(_repository, processor, new DatabaseLocks());

        var result = await queue.RunAsync(databasePath, new AppSettings(), Prepared(jobs));

        processor.Processed.Should().Equal("first.mp4", "moved-next.mp4", "later.mp4", "added.mp4");
        result.ReadyToFinalize.Should().Be(4);
    }

    /// <summary><c>immich run</c> keeps the set its limit chose (until phase A step 7): a video queued meanwhile waits.</summary>
    [Fact]
    public async Task RunAsyncWithAFixedSetTakesOnlyThoseVideos()
    {
        var (databasePath, jobs) = await QueuedAsync("first.mp4", "second.mp4", "outside.mp4");
        var processor = new ScriptedProcessor();
        var queue = new QueueService(_repository, processor, new DatabaseLocks());

        _ = await queue.RunAsync(databasePath, new AppSettings(), Prepared(jobs[..2]) with { FollowQueue = false });

        processor.Processed.Should().Equal("first.mp4", "second.mp4");
    }

    /// <summary>
    /// R3: a video held or dropped from another terminal between the run's read and its first write is passed over, and
    /// counts as started, so a stop after this video pressed meanwhile stops there instead of starting another.
    /// </summary>
    [Fact]
    public async Task RunAsyncPassesOverAVideoThatChangedAndStopsAfterItWhenAskedTo()
    {
        var (databasePath, jobs) = await QueuedAsync("changed.mp4", "next.mp4");
        using var stop = new CancellationTokenSource();
        var processor = new ScriptedProcessor();
        processor.During["changed.mp4"] = () =>
        {
            stop.Cancel();
            throw new JobChangedException(jobs[0].Id);
        };
        var queue = new QueueService(_repository, processor, new DatabaseLocks());

        var result = await queue.RunAsync(databasePath, new AppSettings(), Prepared(jobs), stopAfterThisVideo: stop.Token);

        processor.Processed.Should().Equal("changed.mp4");
        result.FailedJobs.Should().BeEmpty();
        result.StoppedAfter!.Id.Should().Be(jobs[0].Id);
        result.NotStarted.Should().Be(1);
        result.ExitCode.Should().Be(ExitCode.Cancelled);
    }

    /// <summary>A video the caller passes over (it needs a worker this run lacks) is left queued, without a header, and not asked about again.</summary>
    [Fact]
    public async Task RunAsyncLeavesAPassedOverVideoQueued()
    {
        var (databasePath, jobs) = await QueuedAsync("needs-worker.mp4", "local.mp4");
        var processor = new ScriptedProcessor();
        var reported = new List<JobProgress>();
        var asked = new List<string>();
        var queue = new QueueService(_repository, processor, new DatabaseLocks());

        _ = await queue.RunAsync(databasePath, new AppSettings(), Prepared(jobs), (job, _) =>
        {
            asked.Add(Path.GetFileName(job.SourcePath));
            return Task.FromResult(job.Id == jobs[0].Id);
        }, new ListProgress(reported));

        asked.Should().Equal("needs-worker.mp4", "local.mp4");
        processor.Processed.Should().Equal("local.mp4");
        reported.Should().Contain(new JobProgress(JobProgressKind.Header, "[1/1] local.mp4"));
        (await _repository.GetAsync(databasePath, jobs[0].Id))!.Status.Should().Be(JobStatus.Queued);
    }

    /// <summary>
    /// R2: preparation leaves a held job out before anything can write it, so a hold is never undone or the job failed behind
    /// the user's back; the rest come in run order, <c>queue next</c> first.
    /// </summary>
    [Fact]
    public async Task PrepareAsyncLeavesAHeldJobAloneAndOrdersTheRestForTheRun()
    {
        var (databasePath, jobs) = await QueuedAsync("older.mp4", "held-waiting.mp4", "held-started-elsewhere.mp4", "moved-next.mp4");
        // Started on another host: preparation would fail it as a changed configuration if it touched it.
        var heldStarted = jobs[2];
        heldStarted.Status = JobStatus.Encoding;
        heldStarted.Crf = 30;
        heldStarted.ExecutionMode = ProcessingModes.RemoteSsh;
        heldStarted.RemoteHost = "old-host";
        await _repository.UpdateAsync(databasePath, heldStarted);
        heldStarted.Status = JobStatus.Interrupted;
        heldStarted.ResumeStatus = JobStatus.Encoding;
        await _repository.UpdateAsync(databasePath, heldStarted);
        (await _repository.SetHeldAsync(databasePath, heldStarted, held: true)).Should().BeTrue();
        (await _repository.SetHeldAsync(databasePath, jobs[1], held: true)).Should().BeTrue();
        (await _repository.RunNextAsync(databasePath, jobs[3])).Should().BeTrue();
        // Written last, so the database's own order (latest update first) puts it ahead; the run order must not.
        await _repository.UpdateAsync(databasePath, jobs[0]);
        var queue = new QueueService(_repository, new NoopProcessor(), new DatabaseLocks());

        var preparation = await queue.PrepareAsync(databasePath, new AppSettings { Database = new DatabaseSettings { Path = databasePath } }, JobSourceKind.File);

        preparation.RunnableJobs.Select(job => Path.GetFileName(job.SourcePath)).Should().Equal("moved-next.mp4", "older.mp4");
        preparation.FailedJobs.Should().BeEmpty();
        var stored = (await _repository.GetAsync(databasePath, heldStarted.Id))!;
        stored.Status.Should().Be(JobStatus.Interrupted);
        stored.Held.Should().BeTrue();
    }

    /// <summary>R2: a job a verb changes while preparation writes it is passed over; the verb's change stands.</summary>
    [Fact]
    public async Task PrepareAsyncPassesOverAJobChangedWhileItPrepares()
    {
        var (databasePath, jobs) = await QueuedAsync("changed.mp4", "other.mp4");
        // Never started and bound to this PC, so a remote run adopts each with a write.
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = databasePath },
            Processing = new ProcessingSettings { Mode = ProcessingModes.RemoteSsh, RemoteSsh = new RemoteSshSettings { Host = "video-worker", WorkingDirectory = "/w" } }
        };
        var queue = new QueueService(new ChangingOnUpdate(_repository, jobs[0].Id), new NoopProcessor(), new DatabaseLocks());

        var preparation = await queue.PrepareAsync(databasePath, settings, JobSourceKind.File);

        preparation.RunnableJobs.Select(job => Path.GetFileName(job.SourcePath)).Should().Equal("other.mp4");
        (await _repository.GetAsync(databasePath, jobs[0].Id))!.ExecutionMode.Should().Be(ProcessingModes.Local);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class ChangingOnUpdate(IJobRepository inner, Guid jobId) : DelegatingJobRepository(inner)
    {
        public override async Task UpdateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default)
        {
            if (job.Id == jobId) await ChangeBehindTheCommandAsync(databasePath, jobId, cancellationToken);
            await Inner.UpdateAsync(databasePath, job, cancellationToken);
        }
    }

    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    /// <summary>Queued jobs for these files, oldest first, in a fresh database.</summary>
    private async Task<(string DatabasePath, JobRecord[] Jobs)> QueuedAsync(params string[] names)
    {
        var databasePath = Path.Combine(_directory, "jobs.db");
        var jobs = new List<JobRecord>();
        foreach (var name in names)
        {
            jobs.Add(await _repository.CreateAsync(databasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, name), SourceFingerprint = name, Status = JobStatus.Queued }));
            await Task.Delay(5);
        }
        return (databasePath, [.. jobs]);
    }

    private static QueuePreparationResult Prepared(IReadOnlyList<JobRecord> jobs) => new(jobs, [], [], []);

    /// <summary>
    /// Makes the first <paramref name="readyBefore"/> jobs ready, then reports every job after as interrupted by an unreachable
    /// worker. It writes nothing, so a video given twice fails the test rather than loop.
    /// </summary>
    private sealed class UnreachableAfterProcessor(int readyBefore) : IJobProcessor
    {
        private readonly HashSet<Guid> _seen = [];

        public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public int Calls { get; private set; }

        public Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (!_seen.Add(job.Id)) throw new InvalidOperationException($"{job.SourcePath} was given to the processor twice.");
            var outcome = new JobRecord { Id = job.Id, SourcePath = job.SourcePath };
            if (Calls++ < readyBefore)
            {
                outcome.Status = JobStatus.ReadyToFinalize;
                return Task.FromResult(new JobProcessingResult(outcome, ExitCode.Success, "ready"));
            }
            outcome.Status = JobStatus.Interrupted;
            outcome.ResumeStatus = JobStatus.Staging;
            outcome.FailureCategory = FailureCategories.RemoteUnavailable;
            return Task.FromResult(new JobProcessingResult(outcome, ExitCode.ProcessingFailure, "SSH connection failed"));
        }
    }

    private sealed class ResultProcessor(JobProcessingResult result) : IJobProcessor
    {
        public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(true);

        private readonly HashSet<Guid> _seen = [];

        // It writes nothing, so a run that forgot which videos it tried would loop on the same one forever.
        public Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) =>
            _seen.Add(job.Id) ? Task.FromResult(result) : throw new InvalidOperationException($"{job.SourcePath} was given to the processor twice.");
    }

    /// <summary>
    /// Records the order it is given videos in, runs <see cref="During"/>'s step partway through a video, and reports each one
    /// ready without writing it. A video given twice fails the test: a run tries each one once.
    /// </summary>
    private sealed class ScriptedProcessor : IJobProcessor
    {
        private readonly HashSet<Guid> _seen = [];

        public Dictionary<string, Func<Task>> During { get; } = [];
        public List<string> Processed { get; } = [];

        public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public async Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (!_seen.Add(job.Id)) throw new InvalidOperationException($"{job.SourcePath} was given to the processor twice.");
            var name = Path.GetFileName(job.SourcePath);
            Processed.Add(name);
            if (During.TryGetValue(name, out var during)) await during();
            progress?.Report(JobProgress.Step($"processed {name}"));
            return new JobProcessingResult(new JobRecord { Id = job.Id, SourcePath = job.SourcePath, Status = JobStatus.ReadyToFinalize }, ExitCode.Success, "ready");
        }
    }

    private sealed class NoopProcessor : IJobProcessor
    {
        public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
