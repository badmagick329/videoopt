using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class QueueServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public QueueServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task DiscoverAsyncQueuesEligibleFilesOnlyOnce()
    {
        var source = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(source, "source");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var queue = new QueueService(new FixedScanner(source), repository, new FileFingerprintService(), new NoopProcessor());
        var settings = new AppSettings { Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") } };

        var first = await queue.DiscoverAsync(settings.Database.Path, settings, first: false);
        var second = await queue.DiscoverAsync(settings.Database.Path, settings, first: false);

        first.QueuedPaths.Should().ContainSingle();
        second.AlreadyQueued.Should().Be(1);
        var job = (await repository.ListAsync(settings.Database.Path, terminal: false)).Should().ContainSingle(job => job.Status == JobStatus.Queued).Subject;
        job.VideoFacts.Should().Be(new VideoFacts("h264", 1920, 1080, 22_000_000, 90.5, 6));
    }

    [Fact]
    public async Task DiscoverAsyncFirstSkipsAnOpenEligibleSourceAndQueuesTheNextOne()
    {
        var alreadyOpen = Path.Combine(_directory, "already-open.mp4");
        var next = Path.Combine(_directory, "next.mp4");
        var later = Path.Combine(_directory, "later.mp4");
        await File.WriteAllTextAsync(alreadyOpen, "source");
        await File.WriteAllTextAsync(next, "source");
        await File.WriteAllTextAsync(later, "source");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = new AppSettings { Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") } };
        await repository.CreateAsync(settings.Database.Path, new JobRecord { Id = Guid.NewGuid(), SourcePath = alreadyOpen, SourceFingerprint = "open", Status = JobStatus.Queued });
        var scanner = new FirstAwareScanner(alreadyOpen, next, later);
        var queue = new QueueService(scanner, repository, new FileFingerprintService(), new NoopProcessor());

        var result = await queue.DiscoverAsync(settings.Database.Path, settings, first: true);

        scanner.ReceivedOpenPaths.Should().Contain(alreadyOpen);
        result.QueuedPaths.Should().ContainSingle().Which.Should().Be(next);
        result.AlreadyQueued.Should().Be(1);
        (await repository.ListAsync(settings.Database.Path, terminal: false)).Should().HaveCount(2);
    }

    [Fact]
    public async Task DiscoverAsyncWithoutFirstQueuesEveryNewlyEligibleSource()
    {
        var one = Path.Combine(_directory, "one.mp4");
        var two = Path.Combine(_directory, "two.mp4");
        await File.WriteAllTextAsync(one, "source");
        await File.WriteAllTextAsync(two, "source");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = new AppSettings { Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") } };
        var queue = new QueueService(new FixedScanner(one, two), repository, new FileFingerprintService(), new NoopProcessor());

        var result = await queue.DiscoverAsync(settings.Database.Path, settings, first: false);

        result.QueuedPaths.Should().BeEquivalentTo([one, two]);
    }

    [Fact]
    public async Task DiscoverAsyncSkipsAKeptOriginalUntilItChanges()
    {
        var kept = Path.Combine(_directory, "kept.mp4");
        var replaced = Path.Combine(_directory, "replaced.mp4");
        await File.WriteAllTextAsync(kept, "source");
        await File.WriteAllTextAsync(replaced, "source");
        var fingerprints = new FileFingerprintService();
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = new AppSettings { Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") } };
        await repository.CreateAsync(settings.Database.Path, new JobRecord { Id = Guid.NewGuid(), SourcePath = kept, SourceFingerprint = await fingerprints.CreateAsync(kept), SourceSizeBytes = 6, Status = JobStatus.Completed });
        await repository.CreateAsync(settings.Database.Path, new JobRecord { Id = Guid.NewGuid(), SourcePath = replaced, SourceFingerprint = await fingerprints.CreateAsync(replaced), SourceSizeBytes = 6, Status = JobStatus.Completed });
        await File.WriteAllTextAsync(replaced, "av1");
        var queue = new QueueService(new FixedScanner(kept, replaced), repository, fingerprints, new NoopProcessor());

        var unchanged = await queue.DiscoverAsync(settings.Database.Path, settings, first: false);
        await File.WriteAllTextAsync(kept, "edited");
        var changed = await queue.DiscoverAsync(settings.Database.Path, settings, first: false);

        unchanged.AlreadyConverted.Should().Be(1);
        unchanged.QueuedPaths.Should().Equal(replaced);
        changed.AlreadyConverted.Should().Be(0);
        changed.QueuedPaths.Should().Equal(kept);
    }

    [Fact]
    public async Task DiscoverAsyncFirstPassesKeptOriginalsToTheScannerSoItKeepsLooking()
    {
        var kept = Path.Combine(_directory, "kept.mp4");
        var next = Path.Combine(_directory, "next.mp4");
        var later = Path.Combine(_directory, "later.mp4");
        foreach (var path in new[] { kept, next, later }) await File.WriteAllTextAsync(path, "source");
        var fingerprints = new FileFingerprintService();
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = new AppSettings { Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") } };
        await repository.CreateAsync(settings.Database.Path, new JobRecord { Id = Guid.NewGuid(), SourcePath = kept, SourceFingerprint = await fingerprints.CreateAsync(kept), Status = JobStatus.Completed });
        var scanner = new FirstAwareScanner(kept, next, later);
        var queue = new QueueService(scanner, repository, fingerprints, new NoopProcessor());

        var result = await queue.DiscoverAsync(settings.Database.Path, settings, first: true);

        scanner.ReceivedOpenPaths.Should().Contain(kept);
        result.QueuedPaths.Should().Equal(next);
        result.AlreadyConverted.Should().Be(1);
        result.AlreadyQueued.Should().Be(0);
    }

    [Theory]
    [InlineData(JobStatus.Failed, "ValidationFailed")]
    [InlineData(JobStatus.Cancelled, "Cancelled")]
    public async Task DiscoverAsyncSkipsAFailedOrCancelledOriginalUntilItChanges(JobStatus status, string category)
    {
        var source = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(source, "source");
        var fingerprints = new FileFingerprintService();
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = new AppSettings { Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") } };
        await repository.CreateAsync(settings.Database.Path, new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = await fingerprints.CreateAsync(source), Status = status, FailureCategory = category });
        var queue = new QueueService(new FixedScanner(source), repository, fingerprints, new NoopProcessor());

        var unchanged = await queue.DiscoverAsync(settings.Database.Path, settings, first: false);
        await File.WriteAllTextAsync(source, "edited");
        var changed = await queue.DiscoverAsync(settings.Database.Path, settings, first: false);

        unchanged.PreviouslyFailed.Should().Be(1);
        unchanged.QueuedPaths.Should().BeEmpty();
        changed.PreviouslyFailed.Should().Be(0);
        changed.QueuedPaths.Should().Equal(source);
    }

    [Theory]
    [InlineData("SourceNotReady")]
    [InlineData("Ineligible")]
    [InlineData("SourceOutsideWatchRoot")]
    public async Task DiscoverAsyncRequeuesAnUnchangedOriginalWhoseFailureJudgedOnlyItsStateAtRunTime(string category)
    {
        var source = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(source, "source");
        var fingerprints = new FileFingerprintService();
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = new AppSettings { Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") } };
        await repository.CreateAsync(settings.Database.Path, new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = await fingerprints.CreateAsync(source), Status = JobStatus.Failed, FailureCategory = category });
        var queue = new QueueService(new FixedScanner(source), repository, fingerprints, new NoopProcessor());

        var result = await queue.DiscoverAsync(settings.Database.Path, settings, first: false);

        result.PreviouslyFailed.Should().Be(0);
        result.QueuedPaths.Should().Equal(source);
    }

    [Fact]
    public async Task DiscoverAsyncJudgesAnOriginalByItsLatestJob()
    {
        var failedLast = Path.Combine(_directory, "failed-last.mp4");
        var notReadyLast = Path.Combine(_directory, "not-ready-last.mp4");
        await File.WriteAllTextAsync(failedLast, "source");
        await File.WriteAllTextAsync(notReadyLast, "source");
        var fingerprints = new FileFingerprintService();
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = new AppSettings { Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") } };
        foreach (var (path, category) in new[] { (failedLast, "SourceNotReady"), (notReadyLast, "ValidationFailed"), (failedLast, "ValidationFailed"), (notReadyLast, "SourceNotReady") })
        {
            await repository.CreateAsync(settings.Database.Path, new JobRecord { Id = Guid.NewGuid(), SourcePath = path, SourceFingerprint = await fingerprints.CreateAsync(path), Status = JobStatus.Failed, FailureCategory = category });
            await Task.Delay(5);
        }
        var queue = new QueueService(new FixedScanner(failedLast, notReadyLast), repository, fingerprints, new NoopProcessor());

        var result = await queue.DiscoverAsync(settings.Database.Path, settings, first: false);

        result.PreviouslyFailed.Should().Be(1);
        result.QueuedPaths.Should().Equal(notReadyLast);
    }

    [Fact]
    public async Task DiscoverAsyncFirstCountsARetriedOriginalOnlyAsQueued()
    {
        var retried = Path.Combine(_directory, "retried.mp4");
        var next = Path.Combine(_directory, "next.mp4");
        var later = Path.Combine(_directory, "later.mp4");
        foreach (var path in new[] { retried, next, later }) await File.WriteAllTextAsync(path, "source");
        var fingerprints = new FileFingerprintService();
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = new AppSettings { Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") } };
        var fingerprint = await fingerprints.CreateAsync(retried);
        await repository.CreateAsync(settings.Database.Path, new JobRecord { Id = Guid.NewGuid(), SourcePath = retried, SourceFingerprint = fingerprint, Status = JobStatus.Failed, FailureCategory = "ValidationFailed" });
        await repository.CreateAsync(settings.Database.Path, new JobRecord { Id = Guid.NewGuid(), SourcePath = retried, SourceFingerprint = fingerprint, Status = JobStatus.Queued });
        var queue = new QueueService(new FirstAwareScanner(retried, next, later), repository, fingerprints, new NoopProcessor());

        var result = await queue.DiscoverAsync(settings.Database.Path, settings, first: true);

        result.QueuedPaths.Should().Equal(next);
        result.AlreadyQueued.Should().Be(1);
        result.PreviouslyFailed.Should().Be(0);
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("minimumVmaf", false)]
    [InlineData("maxEncodedPercent", false)]
    [InlineData("sampleCount", true)]
    [InlineData("minimumBytesSaved", false)]
    [InlineData("minimumBytesSavedRespelled", true)]
    public async Task DiscoverAsyncSettlesASkippedOriginalOnlyWhileTheQualityBarIsUnchanged(string change, bool stillSettled)
    {
        var source = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(source, "source");
        var fingerprints = new FileFingerprintService();
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = new AppSettings { Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") } };
        await repository.CreateAsync(settings.Database.Path, new JobRecord
        {
            Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = await fingerprints.CreateAsync(source), Status = JobStatus.Skipped,
            FailureCategory = "NotWorthConverting", QualityFingerprint = QualityFingerprint.Compute(settings)
        });
        switch (change)
        {
            case "minimumVmaf": settings.Quality.MinimumVmaf = 93; break;
            case "maxEncodedPercent": settings.Quality.MaxEncodedPercent = 90; break;
            case "sampleCount": settings.Quality.CrfSearch.SampleCount = 9; break;
            case "minimumBytesSaved": settings.Savings.MinimumBytesSaved = "50MiB"; break;
            case "minimumBytesSavedRespelled": settings.Savings.MinimumBytesSaved = "100 MiB"; break;
        }
        var queue = new QueueService(new FixedScanner(source), repository, fingerprints, new NoopProcessor());

        var result = await queue.DiscoverAsync(settings.Database.Path, settings, first: false);

        result.PreviouslySkipped.Should().Be(stillSettled ? 1 : 0);
        result.PreviouslyFailed.Should().Be(0);
        result.QueuedPaths.Should().BeEquivalentTo(stillSettled ? [] : new[] { source });
    }

    [Fact]
    public async Task RunAsyncCountsASkippedJobAsSkippedNotFailedAndExitsSuccessfully()
    {
        var (databasePath, jobs) = await QueuedAsync("a.mp4");
        var skipped = new JobRecord { Id = jobs[0].Id, SourcePath = jobs[0].SourcePath, Status = JobStatus.Skipped, FailureCategory = "NotWorthConverting", FailureMessage = "AV1 would keep 95%." };
        var queue = new QueueService(new FixedScanner(), _repository, new FileFingerprintService(), new ResultProcessor(new JobProcessingResult(skipped, ExitCode.Success, "AV1 would keep 95%.")));

        var result = await queue.RunAsync(databasePath, new AppSettings(), Prepared(jobs));

        result.ReadyToFinalize.Should().Be(0);
        result.FailedJobs.Should().BeEmpty();
        result.SkippedJobs.Should().ContainSingle().Which.Should().Be(new SkippedQueueJob(skipped.Id, skipped.SourcePath, "AV1 would keep 95%."));
        result.ExitCode.Should().Be(ExitCode.Success);
    }

    [Fact]
    public async Task RunAsyncOpensEachJobWithAHeaderNumberedOutOfTheTotalWithItsSize()
    {
        await File.WriteAllBytesAsync(Path.Combine(_directory, "first.mp4"), new byte[2048]);
        var (databasePath, jobs) = await QueuedAsync("first.mp4", "gone.mp4");
        var reported = new List<JobProgress>();
        var queue = new QueueService(new FixedScanner(), _repository, new FileFingerprintService(), new ScriptedProcessor());

        _ = await queue.RunAsync(databasePath, new AppSettings(), Prepared(jobs), progress: new ListProgress(reported));

        // Each header precedes its own job's lines, and a missing original gets no size rather than a made-up one.
        reported.Should().Equal(
            new JobProgress(JobProgressKind.Header, "[1/2] first.mp4 (2.0 KiB)"),
            JobProgress.Step("processed first.mp4"),
            new JobProgress(JobProgressKind.Header, "[2/2] gone.mp4"),
            JobProgress.Step("processed gone.mp4"));
    }

    /// <summary>
    /// Every later job would retry the dead worker and fail the same way, so the run stops at the first unreachable result.
    /// That job resumes, so it is not a failure, but the exit code counts it as one, as before (VOPT-69).
    /// </summary>
    [Theory]
    [InlineData(0, 2, ExitCode.ProcessingFailure)]
    [InlineData(1, 1, ExitCode.PartialSuccess)]
    public async Task RunAsyncStopsAtTheFirstJobWhoseWorkerCannotBeReached(int readyBefore, int notStarted, ExitCode exitCode)
    {
        var (databasePath, jobs) = await QueuedAsync("1.mp4", "2.mp4", "3.mp4");
        var processor = new UnreachableAfterProcessor(readyBefore);
        var queue = new QueueService(new FixedScanner(), _repository, new FileFingerprintService(), processor);

        var result = await queue.RunAsync(databasePath, new AppSettings(), Prepared(jobs));

        processor.Calls.Should().Be(readyBefore + 1);
        result.UnreachableWorkerJob!.SourcePath.Should().Be(jobs[readyBefore].SourcePath);
        result.NotStarted.Should().Be(notStarted);
        result.FailedJobs.Should().BeEmpty();
        result.ReadyToFinalize.Should().Be(readyBefore);
        result.ExitCode.Should().Be(exitCode);
    }

    /// <summary>
    /// The user's choice (VOPT-54 step 5): a run takes each video as it starts it, so a hold, a <c>queue next</c> and a video
    /// queued from another terminal while one converts all count at the next video.
    /// </summary>
    [Fact]
    public async Task RunAsyncTakesTheQueueAsItIsBeforeEachVideo()
    {
        var (databasePath, jobs) = await QueuedAsync("first.mp4", "held.mp4", "later.mp4", "moved-next.mp4");
        // A run kept to folder videos (Immich can't be used) leaves this one queued.
        await _repository.CreateAsync(databasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "immich.mp4"), SourceFingerprint = "immich", Status = JobStatus.Queued, SourceKind = JobSourceKind.Immich, ImmichAssetId = "asset" });
        var processor = new ScriptedProcessor();
        processor.During["first.mp4"] = async () =>
        {
            (await _repository.SetHeldAsync(databasePath, (await _repository.GetAsync(databasePath, jobs[1].Id))!, held: true)).Should().BeTrue();
            (await _repository.RunNextAsync(databasePath, (await _repository.GetAsync(databasePath, jobs[3].Id))!)).Should().BeTrue();
            await _repository.CreateAsync(databasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "added.mp4"), SourceFingerprint = "added", Status = JobStatus.Queued });
        };
        var queue = new QueueService(new FixedScanner(), _repository, new FileFingerprintService(), processor);

        var result = await queue.RunAsync(databasePath, new AppSettings(), Prepared(jobs) with { SourceKind = JobSourceKind.File });

        processor.Processed.Should().Equal("first.mp4", "moved-next.mp4", "later.mp4", "added.mp4");
        result.ReadyToFinalize.Should().Be(4);
    }

    /// <summary><c>run</c> converts every source: an Immich video takes its place in run order beside folder videos.</summary>
    [Fact]
    public async Task RunAsyncWithNoSourceTakesEverySourceInRunOrder()
    {
        var (databasePath, jobs) = await QueuedAsync("first.mp4");
        var immich = await _repository.CreateAsync(databasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "IMG_0001.MOV"), SourceFingerprint = "immich", Status = JobStatus.Queued, SourceKind = JobSourceKind.Immich, ImmichAssetId = "asset" });
        await Task.Delay(5);
        var (_, later) = await QueuedAsync("later.mp4");
        var processor = new ScriptedProcessor();
        var queue = new QueueService(new FixedScanner(), _repository, new FileFingerprintService(), processor);

        _ = await queue.RunAsync(databasePath, new AppSettings(), Prepared([.. jobs, immich, .. later]));

        processor.Processed.Should().Equal("first.mp4", "IMG_0001.MOV", "later.mp4");
    }

    /// <summary><c>immich run</c> keeps the set its limit chose (until phase A step 7): a video queued meanwhile waits.</summary>
    [Fact]
    public async Task RunAsyncWithAFixedSetTakesOnlyThoseVideos()
    {
        var (databasePath, jobs) = await QueuedAsync("first.mp4", "second.mp4", "outside.mp4");
        var processor = new ScriptedProcessor();
        var queue = new QueueService(new FixedScanner(), _repository, new FileFingerprintService(), processor);

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
        var queue = new QueueService(new FixedScanner(), _repository, new FileFingerprintService(), processor);

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
        var queue = new QueueService(new FixedScanner(), _repository, new FileFingerprintService(), processor);

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
        var queue = new QueueService(new FixedScanner(), _repository, new FileFingerprintService(), new NoopProcessor());

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
        var queue = new QueueService(new FixedScanner(), new ChangingOnUpdate(_repository, jobs[0].Id), new FileFingerprintService(), new NoopProcessor());

        var preparation = await queue.PrepareAsync(databasePath, settings, JobSourceKind.File);

        preparation.RunnableJobs.Select(job => Path.GetFileName(job.SourcePath)).Should().Equal("other.mp4");
        (await _repository.GetAsync(databasePath, jobs[0].Id))!.ExecutionMode.Should().Be(ProcessingModes.Local);
    }

    /// <summary>R11: another command queued the file between discovery's check and its insert; it counts as already queued.</summary>
    [Fact]
    public async Task DiscoverAsyncCountsAFileQueuedMeanwhileAsAlreadyQueued()
    {
        var (databasePath, jobs) = await QueuedAsync("movie.mp4");
        await File.WriteAllTextAsync(jobs[0].SourcePath, "source");
        var queue = new QueueService(new FixedScanner(jobs[0].SourcePath), new BlindToOpenJobs(_repository), new FileFingerprintService(), new NoopProcessor());

        var result = await queue.DiscoverAsync(databasePath, new AppSettings { Database = new DatabaseSettings { Path = databasePath } }, first: false);

        result.AlreadyQueued.Should().Be(1);
        result.QueuedPaths.Should().BeEmpty();
        result.Issues.Should().Be(0);
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

    /// <summary>Reads as if no job were open yet, as a command that read just before another queued the file would.</summary>
    private sealed class BlindToOpenJobs(IJobRepository inner) : DelegatingJobRepository(inner)
    {
        public override Task<JobRecord?> FindOpenBySourceAsync(string databasePath, string sourcePath, CancellationToken cancellationToken = default) => Task.FromResult<JobRecord?>(null);

        public override async Task<IReadOnlyList<JobRecord>> ListAsync(string databasePath, bool terminal, CancellationToken cancellationToken = default) =>
            terminal ? await Inner.ListAsync(databasePath, terminal, cancellationToken) : [];
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

    /// <summary>Makes the first <paramref name="readyBefore"/> jobs ready, then reports every job after as interrupted by an unreachable worker.</summary>
    private sealed class UnreachableAfterProcessor(int readyBefore) : IJobProcessor
    {
        public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public int Calls { get; private set; }

        public Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var outcome = new JobRecord { Id = job.Id, SourcePath = job.SourcePath };
            if (Calls++ < readyBefore)
            {
                outcome.Status = JobStatus.ReadyToFinalize;
                return Task.FromResult(new JobProcessingResult(outcome, ExitCode.Success, "ready"));
            }
            outcome.Status = JobStatus.Interrupted;
            outcome.ResumeStatus = JobStatus.Staging;
            outcome.FailureCategory = JobProcessor.RemoteUnavailableCategory;
            return Task.FromResult(new JobProcessingResult(outcome, ExitCode.ProcessingFailure, "SSH connection failed"));
        }
    }

    /// <summary>An eligible item as the real scanner gives it: with the probe it was judged on and the file's size.</summary>
    private static ScanItem Eligible(string path) => new(path, ScanItemStatus.Eligible, "Eligible", 6, ScannedMedia);

    private static readonly MediaInfo ScannedMedia = new("h264", 1, 1, 0, 0, 90.5, 6, 1920, 1080, 22_000_000);

    private sealed class FixedScanner(params string[] paths) : IFileScanner
    {
        public Task<ScanReport> ScanAsync(IReadOnlyList<WatchRootSettings> roots, AppSettings settings, bool stopAfterFirstEligible = false, IReadOnlySet<string>? openSourcePaths = null, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new ScanReport(paths.Select(Eligible).ToArray(), []));
        public Task<FileVerdict> JudgeAsync(string path, AppSettings settings, bool force, CancellationToken cancellationToken = default) => throw new NotSupportedException("Not a queue add <file> test.");
    }

    private sealed class FirstAwareScanner(string alreadyOpen, string next, string later) : IFileScanner
    {
        public IReadOnlySet<string>? ReceivedOpenPaths { get; private set; }

        public Task<ScanReport> ScanAsync(IReadOnlyList<WatchRootSettings> roots, AppSettings settings, bool stopAfterFirstEligible = false, IReadOnlySet<string>? openSourcePaths = null, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            ReceivedOpenPaths = openSourcePaths;
            return Task.FromResult(new ScanReport(
                [Eligible(alreadyOpen), Eligible(next), Eligible(later)],
                []));
        }
        public Task<FileVerdict> JudgeAsync(string path, AppSettings settings, bool force, CancellationToken cancellationToken = default) => throw new NotSupportedException("Not a queue add <file> test.");
    }

    private sealed class ResultProcessor(JobProcessingResult result) : IJobProcessor
    {
        public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(true);

        private readonly HashSet<Guid> _seen = [];

        // It writes nothing, so a run that forgot which videos it tried would loop on the same one forever.
        public Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) =>
            _seen.Add(job.Id) ? Task.FromResult(result) : throw new InvalidOperationException($"{job.SourcePath} was given to the processor twice.");
    }

    private sealed class ListProgress(List<JobProgress> lines) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => lines.Add(value);
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
