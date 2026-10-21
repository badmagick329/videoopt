using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;

namespace VideoOptimiser.UnitTests.Features.Queue;

public sealed class QueueDiscoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FileFingerprintService _fingerprints = new();
    private readonly AppSettings _settings;

    public QueueDiscoveryTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new AppSettings { Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") } };
    }

    [Fact]
    public async Task DiscoverAsyncQueuesEligibleFilesOnlyOnce()
    {
        var paths = await FilesAsync("one.mp4", "two.mp4");
        var scanner = new FixedScanner(paths);

        var first = await DiscoverAsync(scanner);
        var second = await DiscoverAsync(scanner);

        first.QueuedPaths.Should().BeEquivalentTo(paths);
        second.QueuedPaths.Should().BeEmpty();
        second.AlreadyQueued.Should().Be(2);
        var jobs = await _repository.ListAsync(_settings.Database.Path, terminal: false);
        jobs.Should().HaveCount(2).And.OnlyContain(job => job.Status == JobStatus.Queued && job.VideoFacts == new VideoFacts("h264", 1920, 1080, 22_000_000, 90.5, 6));
    }

    /// <summary>
    /// The limit counts new videos in scan order: the open and the kept file come first and never count, and the scan stops
    /// once it has met the limit, so later files are never queued. A file that fails to queue after the scan counted it
    /// (deleted here) leaves fewer than the limit, and no rescan makes up for it.
    /// </summary>
    [Theory]
    [InlineData(null, false, "a,b,c", 0)]
    [InlineData(1, false, "a", 0)]
    [InlineData(2, false, "a,b", 0)]
    [InlineData(5, false, "a,b,c", 0)]
    [InlineData(2, true, "b", 1)]
    public async Task DiscoverAsyncQueuesAtMostTheLimitOfNewVideos(int? limit, bool firstNewFails, string queued, int issues)
    {
        var paths = await FilesAsync("open.mp4", "kept.mp4", "a.mp4", "b.mp4", "c.mp4");
        await JobAsync(paths[0], JobStatus.Queued);
        await JobAsync(paths[1], JobStatus.Completed);
        if (firstNewFails) File.Delete(paths[2]);
        var scanner = new FixedScanner(paths);

        var result = await DiscoverAsync(scanner, limit);

        scanner.ReceivedStopAfter.Should().Be(limit);
        if (limit is not null) scanner.ReceivedOpenPaths.Should().BeEquivalentTo(paths[..2]);
        result.QueuedPaths.Select(Path.GetFileNameWithoutExtension).Should().Equal(queued.Split(','));
        (result.AlreadyQueued, result.AlreadyConverted, result.Issues).Should().Be((1, 1, issues));
    }

    /// <summary>
    /// The unchanged half is a size match with a fingerprint match; the replaced file is skipped by its size alone. The
    /// edited one keeps its size, so only the fingerprint tells it from the original.
    /// </summary>
    [Fact]
    public async Task DiscoverAsyncSkipsAKeptOriginalUntilItChanges()
    {
        var (kept, replaced) = await TwoFilesAsync("kept.mp4", "replaced.mp4");
        await JobAsync(kept, JobStatus.Completed, job => job.SourceSizeBytes = 6);
        await JobAsync(replaced, JobStatus.Completed, job => job.SourceSizeBytes = 6);
        await File.WriteAllTextAsync(replaced, "av1");
        var scanner = new FixedScanner(kept, replaced);

        var unchanged = await DiscoverAsync(scanner);
        await File.WriteAllTextAsync(kept, "edited");
        var changed = await DiscoverAsync(scanner);

        unchanged.AlreadyConverted.Should().Be(1);
        unchanged.QueuedPaths.Should().Equal(replaced);
        changed.AlreadyConverted.Should().Be(0);
        changed.QueuedPaths.Should().Equal(kept);
    }

    /// <summary>A cancelled original takes the same branch; and the file changing is <see cref="DiscoverAsyncSkipsAKeptOriginalUntilItChanges"/>.</summary>
    [Fact]
    public async Task DiscoverAsyncCountsAFailedOriginalAsPreviouslyFailedAndLeavesItOut()
    {
        var source = (await FilesAsync("movie.mp4"))[0];
        await JobAsync(source, JobStatus.Failed, job => job.FailureCategory = "ValidationFailed");

        var result = await DiscoverAsync(new FixedScanner(source));

        result.PreviouslyFailed.Should().Be(1);
        result.QueuedPaths.Should().BeEmpty();
    }

    /// <summary>VOPT-112: a rejected video is the user's verdict, not a failure, so it has its own count; a changed file is a new video and is queued.</summary>
    [Fact]
    public async Task DiscoverAsyncLeavesARejectedOriginalOutUntilItChangesAndCountsItApartFromFailures()
    {
        var source = (await FilesAsync("movie.mp4"))[0];
        await JobAsync(source, JobStatus.Rejected, job => (job.FailureCategory, job.SourceSizeBytes) = (FailureCategories.Rejected, 6));
        var scanner = new FixedScanner(source);

        var unchanged = await DiscoverAsync(scanner);
        await File.WriteAllTextAsync(source, "edited");
        var changed = await DiscoverAsync(scanner);

        unchanged.PreviouslyRejected.Should().Be(1);
        unchanged.PreviouslyFailed.Should().Be(0);
        unchanged.QueuedPaths.Should().BeEmpty();
        changed.PreviouslyRejected.Should().Be(0);
        changed.QueuedPaths.Should().Equal(source);
    }

    [Fact]
    public async Task DiscoverAsyncJudgesAnOriginalByItsLatestJob()
    {
        var (failedLast, notReadyLast) = await TwoFilesAsync("failed-last.mp4", "not-ready-last.mp4");
        foreach (var (path, category) in new[] { (failedLast, "SourceNotReady"), (notReadyLast, "ValidationFailed"), (failedLast, "ValidationFailed"), (notReadyLast, "SourceNotReady") })
        {
            await JobAsync(path, JobStatus.Failed, job => job.FailureCategory = category);
            await Task.Delay(5);
        }

        var result = await DiscoverAsync(new FixedScanner(failedLast, notReadyLast));

        result.PreviouslyFailed.Should().Be(1);
        result.QueuedPaths.Should().Equal(notReadyLast);
    }

    [Fact]
    public async Task DiscoverAsyncCountsARetriedOriginalOnlyAsQueued()
    {
        var retried = (await FilesAsync("retried.mp4"))[0];
        await JobAsync(retried, JobStatus.Failed, job => job.FailureCategory = "ValidationFailed");
        await JobAsync(retried, JobStatus.Queued);

        var result = await DiscoverAsync(new FixedScanner(retried));

        result.QueuedPaths.Should().BeEmpty();
        result.AlreadyQueued.Should().Be(1);
        result.PreviouslyFailed.Should().Be(0);
    }

    /// <summary>The quality settings that decide what is worth converting; the others (VMAF, max encoded) are more fields in the same fingerprint.</summary>
    [Theory]
    [InlineData("same", true)]
    [InlineData("sampleCount", true)]
    [InlineData("minimumBytesSaved", false)]
    [InlineData("minimumBytesSavedRespelled", true)]
    [InlineData("unsupportedAudioDrop", true)]
    [InlineData("unsupportedAudioSkip", false)]
    public async Task DiscoverAsyncSettlesASkippedOriginalOnlyWhileTheQualityBarIsUnchanged(string change, bool stillSettled)
    {
        var source = (await FilesAsync("movie.mp4"))[0];
        await JobAsync(source, JobStatus.Skipped, job => (job.FailureCategory, job.QualityFingerprint) = ("NotWorthConverting", QualityFingerprint.Compute(_settings)));
        switch (change)
        {
            case "sampleCount": _settings.Quality.CrfSearch.ConfiguredSampleCount = 9; break;
            case "minimumBytesSaved": _settings.Savings.MinimumBytesSaved = "50MiB"; break;
            case "minimumBytesSavedRespelled": _settings.Savings.MinimumBytesSaved = "1 MiB"; break;
            // The default adds nothing to the fingerprint, so upgrading leaves every earlier skip settled.
            case "unsupportedAudioDrop": _settings.Quality.UnsupportedAudio = "Drop"; break;
            case "unsupportedAudioSkip": _settings.Quality.UnsupportedAudio = "skip"; break;
        }

        var result = await DiscoverAsync(new FixedScanner(source));

        result.PreviouslySkipped.Should().Be(stillSettled ? 1 : 0);
        result.PreviouslyFailed.Should().Be(0);
        result.QueuedPaths.Should().BeEquivalentTo(stillSettled ? [] : new[] { source });
    }

    /// <summary>R11: another command queued the file between discovery's check and its insert; it counts as already queued.</summary>
    [Fact]
    public async Task DiscoverAsyncCountsAFileQueuedMeanwhileAsAlreadyQueued()
    {
        var source = (await FilesAsync("movie.mp4"))[0];
        await JobAsync(source, JobStatus.Queued);

        var result = await DiscoverAsync(new FixedScanner(source), repository: new BlindToOpenJobs(_repository));

        result.AlreadyQueued.Should().Be(1);
        result.QueuedPaths.Should().BeEmpty();
        result.Issues.Should().Be(0);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private Task<QueueDiscoveryResult> DiscoverAsync(IFileScanner scanner, int? limit = null, IJobRepository? repository = null) =>
        new QueueDiscovery(scanner, repository ?? _repository, _fingerprints, new DatabaseLocks()).DiscoverAsync(_settings.Database.Path, _settings, limit);

    /// <summary>Source files with these names, holding six bytes.</summary>
    private async Task<string[]> FilesAsync(params string[] names)
    {
        var paths = names.Select(name => Path.Combine(_directory, name)).ToArray();
        foreach (var path in paths) await File.WriteAllTextAsync(path, "source");
        return paths;
    }

    private async Task<(string First, string Second)> TwoFilesAsync(string first, string second)
    {
        var paths = await FilesAsync(first, second);
        return (paths[0], paths[1]);
    }

    /// <summary>A job on this file with the fingerprint it has now.</summary>
    private async Task JobAsync(string path, JobStatus status, Action<JobRecord>? configure = null)
    {
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = path, SourceFingerprint = await _fingerprints.CreateAsync(path), Status = status };
        configure?.Invoke(job);
        await _repository.CreateAsync(_settings.Database.Path, job);
    }

    /// <summary>Reads as if no job were open yet, as a command that read just before another queued the file would.</summary>
    private sealed class BlindToOpenJobs(IJobRepository inner) : DelegatingJobRepository(inner)
    {
        public override Task<JobRecord?> FindOpenBySourceAsync(string databasePath, string sourcePath, CancellationToken cancellationToken = default) => Task.FromResult<JobRecord?>(null);

        public override async Task<IReadOnlyList<JobRecord>> ListAsync(string databasePath, bool terminal, CancellationToken cancellationToken = default) =>
            terminal ? await Inner.ListAsync(databasePath, terminal, cancellationToken) : [];
    }

    /// <summary>
    /// Every path as an eligible item as the real scanner gives it: with the probe it was judged on and the file's size. It
    /// stops as the real one does, once it has met the asked number of paths outside the open ones.
    /// </summary>
    private sealed class FixedScanner(params string[] paths) : IFileScanner
    {
        private static readonly MediaInfo ScannedMedia = new("h264", 1, 1, 0, 0, 90.5, 6, 1920, 1080, 22_000_000);

        public IReadOnlySet<string>? ReceivedOpenPaths { get; private set; }
        public int? ReceivedStopAfter { get; private set; }

        public Task<ScanReport> ScanAsync(IReadOnlyList<WatchRootSettings> roots, AppSettings settings, int? stopAfterEligible = null, IReadOnlySet<string>? openSourcePaths = null, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            (ReceivedOpenPaths, ReceivedStopAfter) = (openSourcePaths, stopAfterEligible);
            var items = new List<ScanItem>();
            var found = 0;
            foreach (var path in paths)
            {
                items.Add(new ScanItem(path, ScanItemStatus.Eligible, "Eligible", 6, ScannedMedia));
                if (openSourcePaths?.Contains(path) != true && ++found == stopAfterEligible) break;
            }
            return Task.FromResult(new ScanReport(items, []));
        }

        public Task<FileVerdict> JudgeAsync(string path, AppSettings settings, bool force, CancellationToken cancellationToken = default) => throw new NotSupportedException("Not a queue add <file> test.");
    }
}
