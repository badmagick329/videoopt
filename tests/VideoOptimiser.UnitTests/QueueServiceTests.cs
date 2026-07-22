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

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class FixedScanner(string path) : IFileScanner
    {
        public Task<ScanReport> ScanAsync(IReadOnlyList<WatchRootSettings> roots, AppSettings settings, bool stopAfterFirstEligible = false, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new ScanReport([new ScanItem(path, ScanItemStatus.Eligible, "Eligible")], []));
    }

    private sealed class NoopProcessor : IJobProcessor
    {
        public Task<JobProcessingResult> ProcessAsync(string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
