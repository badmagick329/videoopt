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
        (await repository.ListAsync(settings.Database.Path, terminal: false)).Should().ContainSingle(job => job.Status == JobStatus.Queued);
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

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class FixedScanner(params string[] paths) : IFileScanner
    {
        public Task<ScanReport> ScanAsync(IReadOnlyList<WatchRootSettings> roots, AppSettings settings, bool stopAfterFirstEligible = false, IReadOnlySet<string>? openSourcePaths = null, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new ScanReport(paths.Select(path => new ScanItem(path, ScanItemStatus.Eligible, "Eligible")).ToArray(), []));
    }

    private sealed class FirstAwareScanner(string alreadyOpen, string next, string later) : IFileScanner
    {
        public IReadOnlySet<string>? ReceivedOpenPaths { get; private set; }

        public Task<ScanReport> ScanAsync(IReadOnlyList<WatchRootSettings> roots, AppSettings settings, bool stopAfterFirstEligible = false, IReadOnlySet<string>? openSourcePaths = null, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            ReceivedOpenPaths = openSourcePaths;
            return Task.FromResult(new ScanReport(
                [new ScanItem(alreadyOpen, ScanItemStatus.Eligible, "Eligible"), new ScanItem(next, ScanItemStatus.Eligible, "Eligible"), new ScanItem(later, ScanItemStatus.Eligible, "Eligible")],
                []));
        }
    }

    private sealed class NoopProcessor : IJobProcessor
    {
        public Task<JobProcessingResult> ProcessAsync(string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
