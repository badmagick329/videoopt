using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Scanning;

namespace VideoOptimiser.UnitTests;

public sealed class SqliteMediaProbeCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public SqliteMediaProbeCacheTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task LoadAllAsyncReturnsStoredEntriesWithEveryMetadataField()
    {
        var cache = Cache();
        var path = Path.Combine(_directory, "Movie.mkv");
        var media = new MediaInfo("h264", 1, 2, 3, 4, 123.45, 678, 1920, 1080, 9_000_000);

        await cache.StoreAsync(DatabasePath, new MediaProbeCacheEntry(path, 42, 99, media));

        var entry = (await cache.LoadAllAsync(DatabasePath)).Should().ContainSingle().Subject;

        entry.SourcePath.Should().Be(path);
        entry.SourceSizeBytes.Should().Be(42);
        entry.SourceLastWriteUtcTicks.Should().Be(99);
        entry.MediaInfo.Should().Be(media);
    }

    [Fact]
    public async Task ScanAsyncReusesCachedProbeAndReevaluatesCurrentEligibilityRules()
    {
        var path = await WriteVideoAsync("movie.mkv", [1, 2, 3]);
        var probe = new CountingProbe();
        var scanner = Scanner(probe);
        var eligible = Settings();

        var first = await scanner.ScanAsync(eligible.Watch.Roots, eligible);
        var changedRules = Settings(codecs: ["hevc"]);
        var second = await scanner.ScanAsync(changedRules.Watch.Roots, changedRules);

        first.RealProbes.Should().Be(1);
        second.CacheHits.Should().Be(1);
        probe.Calls.Should().Be(1);
        second.Items.Should().ContainSingle(item => item.Path == path && item.Status == ScanItemStatus.Ineligible);
    }

    [Fact]
    public async Task ScanAsyncReprobesAndReplacesCacheWhenFileIdentityChanges()
    {
        var path = await WriteVideoAsync("movie.mkv", [1, 2, 3]);
        var probe = new CountingProbe();
        var scanner = Scanner(probe);
        var settings = Settings();

        await scanner.ScanAsync(settings.Watch.Roots, settings);
        await File.WriteAllBytesAsync(path, [4, 5, 6, 7]);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        var second = await scanner.ScanAsync(settings.Watch.Roots, settings);
        var info = new FileInfo(path);

        second.RealProbes.Should().Be(1);
        probe.Calls.Should().Be(2);
        (await Cache().LoadAllAsync(DatabasePath)).Should().ContainSingle(entry =>
            entry.SourcePath == path && entry.SourceSizeBytes == info.Length && entry.SourceLastWriteUtcTicks == info.LastWriteTimeUtc.Ticks);
    }

    [Fact]
    public async Task ScanAsyncReprobesWhenOnlyLastWriteTimeChanges()
    {
        var path = await WriteVideoAsync("touched.mkv", [1, 2, 3]);
        var probe = new CountingProbe();
        var scanner = Scanner(probe);
        var settings = Settings();

        await scanner.ScanAsync(settings.Watch.Roots, settings);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        var second = await scanner.ScanAsync(settings.Watch.Roots, settings);

        second.RealProbes.Should().Be(1);
        probe.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ScanAsyncDoesNotCacheFailedOrNotReadySources()
    {
        var failedPath = await WriteVideoAsync("failed.mkv", [1]);
        var failedSettings = Settings();
        var failedScanner = new FileScanner(new Ready(), _ => new ThrowingProbe(), Cache());
        await failedScanner.ScanAsync(failedSettings.Watch.Roots, failedSettings);

        var notReadyPath = await WriteVideoAsync("not-ready.mkv", [2]);
        var notReadySettings = Settings();
        var notReadyScanner = new FileScanner(new Ready(false), _ => new CountingProbe(), Cache());
        await notReadyScanner.ScanAsync(notReadySettings.Watch.Roots, notReadySettings);

        var failedInfo = new FileInfo(failedPath);
        var notReadyInfo = new FileInfo(notReadyPath);
        (await Cache().LoadAllAsync(DatabasePath)).Should().NotContain(entry =>
            entry.SourcePath == failedPath && entry.SourceSizeBytes == failedInfo.Length && entry.SourceLastWriteUtcTicks == failedInfo.LastWriteTimeUtc.Ticks);
        (await Cache().LoadAllAsync(DatabasePath)).Should().NotContain(entry =>
            entry.SourcePath == notReadyPath && entry.SourceSizeBytes == notReadyInfo.Length && entry.SourceLastWriteUtcTicks == notReadyInfo.LastWriteTimeUtc.Ticks);
    }

    [Fact]
    public async Task ScanAsyncDoesNotCacheOrQueueAFileThatChangesDuringProbing()
    {
        var path = await WriteVideoAsync("changing.mkv", [1, 2, 3]);
        var original = new FileInfo(path);
        var settings = Settings();
        var scanner = new FileScanner(new Ready(), _ => new ChangingProbe(), Cache());

        var report = await scanner.ScanAsync(settings.Watch.Roots, settings);

        report.Items.Should().ContainSingle(item => item.Path == path && item.Status == ScanItemStatus.Unavailable && item.Reason == "Source changed while probing.");
        (await Cache().LoadAllAsync(DatabasePath)).Should().NotContain(entry =>
            entry.SourcePath == path && entry.SourceSizeBytes == original.Length && entry.SourceLastWriteUtcTicks == original.LastWriteTimeUtc.Ticks);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");
    private static SqliteMediaProbeCache Cache() => new(new SqliteDatabaseInitializer());
    private static FileScanner Scanner(CountingProbe probe) => new(new Ready(), _ => probe, Cache());

    private AppSettings Settings(IEnumerable<string>? codecs = null) => new()
    {
        Database = new DatabaseSettings { Path = DatabasePath },
        Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
        Eligibility = new EligibilitySettings
        {
            Rules = [new EligibilityRuleSettings { Codecs = (codecs ?? ["h264"]).ToList(), Resolution = "1080p-1440p", MinimumFileSize = "1B", MinimumVideoBitrate = "1Mbps" }]
        }
    };

    private async Task<string> WriteVideoAsync(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private sealed class Ready(bool isReady = true) : IFileReadinessService
    {
        public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new FileReadinessResult(isReady, isReady ? "Readable." : "Not ready."));
    }

    private sealed class CountingProbe : IMediaProbe
    {
        public int Calls { get; private set; }
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new MediaInfo("h264", 1, 2, 3, 4, 120.5, new FileInfo(path).Length, 1920, 1080, 10_000_000));
        }
    }

    private sealed class ThrowingProbe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidDataException("Probe failed.");
    }

    private sealed class ChangingProbe : IMediaProbe
    {
        public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            await File.AppendAllBytesAsync(path, [4], cancellationToken);
            return new MediaInfo("h264", 1, 0, 0, 0, null, null, 1920, 1080, 10_000_000);
        }
    }
}
