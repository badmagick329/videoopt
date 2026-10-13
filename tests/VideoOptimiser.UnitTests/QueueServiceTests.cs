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
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var skipped = new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "a.mp4"), Status = JobStatus.Skipped, FailureCategory = "NotWorthConverting", FailureMessage = "AV1 would keep 95%." };
        var queue = new QueueService(new FixedScanner(), repository, new FileFingerprintService(), new ResultProcessor(new JobProcessingResult(skipped, ExitCode.Success, "AV1 would keep 95%.")));

        var result = await queue.RunAsync(Path.Combine(_directory, "jobs.db"), new AppSettings(), [skipped]);

        result.ReadyToFinalize.Should().Be(0);
        result.FailedJobs.Should().BeEmpty();
        result.SkippedJobs.Should().ContainSingle().Which.Should().Be(new SkippedQueueJob(skipped.Id, skipped.SourcePath, "AV1 would keep 95%."));
        result.ExitCode.Should().Be(ExitCode.Success);
    }

    [Fact]
    public async Task RunAsyncOpensEachJobWithAHeaderNumberedOutOfTheTotalWithItsSize()
    {
        var first = Path.Combine(_directory, "first.mp4");
        var second = Path.Combine(_directory, "gone.mp4");
        await File.WriteAllBytesAsync(first, new byte[2048]);
        var jobs = new[] { first, second }.Select(path => new JobRecord { Id = Guid.NewGuid(), SourcePath = path, Status = JobStatus.Queued }).ToArray();
        var reported = new List<JobProgress>();
        var queue = new QueueService(new FixedScanner(), new SqliteJobRepository(new SqliteDatabaseInitializer()), new FileFingerprintService(), new ReportingProcessor());

        _ = await queue.RunAsync(Path.Combine(_directory, "jobs.db"), new AppSettings(), jobs, new ListProgress(reported));

        // Each header precedes its own job's lines, and a missing original gets no size rather than a made-up one.
        reported.Should().Equal(
            new JobProgress(JobProgressKind.Header, "[1/2] first.mp4 (2.0 KiB)"),
            JobProgress.Step("processed first.mp4"),
            new JobProgress(JobProgressKind.Header, "[2/2] gone.mp4"),
            JobProgress.Step("processed gone.mp4"));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>An eligible item as the real scanner gives it: with the probe it was judged on and the file's size.</summary>
    private static ScanItem Eligible(string path) => new(path, ScanItemStatus.Eligible, "Eligible", 6, ScannedMedia);

    private static readonly MediaInfo ScannedMedia = new("h264", 1, 1, 0, 0, 90.5, 6, 1920, 1080, 22_000_000);

    private sealed class FixedScanner(params string[] paths) : IFileScanner
    {
        public Task<ScanReport> ScanAsync(IReadOnlyList<WatchRootSettings> roots, AppSettings settings, bool stopAfterFirstEligible = false, IReadOnlySet<string>? openSourcePaths = null, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new ScanReport(paths.Select(Eligible).ToArray(), []));
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
    }

    private sealed class ResultProcessor(JobProcessingResult result) : IJobProcessor
    {
        public Task<JobProcessingResult> ProcessAsync(string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class ListProgress(List<JobProgress> lines) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => lines.Add(value);
    }

    private sealed class ReportingProcessor : IJobProcessor
    {
        public Task<JobProcessingResult> ProcessAsync(string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(JobProgress.Step($"processed {Path.GetFileName(sourcePath)}"));
            return Task.FromResult(new JobProcessingResult(new JobRecord { Id = Guid.NewGuid(), SourcePath = sourcePath, Status = JobStatus.ReadyToFinalize }, ExitCode.Success, "ready"));
        }
    }

    private sealed class NoopProcessor : IJobProcessor
    {
        public Task<JobProcessingResult> ProcessAsync(string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
