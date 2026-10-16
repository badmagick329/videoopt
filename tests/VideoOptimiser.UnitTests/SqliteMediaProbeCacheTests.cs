using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Infrastructure.Persistence;

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

    /// <summary>
    /// Version 1 read no bitrate for a video stream without its own. Those rows must be probed again; the rest read the
    /// same under version 2 and stay, so the upgrade does not re-probe a whole library.
    /// </summary>
    [Fact]
    public async Task LoadAllAsyncDropsVersionOneRowsWithoutABitrateOnly()
    {
        var cache = Cache();
        var withBitrate = Path.Combine(_directory, "with.mkv");
        var withoutBitrate = Path.Combine(_directory, "without.mkv");
        await cache.StoreAsync(DatabasePath, new MediaProbeCacheEntry(withBitrate, 1, 1, new MediaInfo("h264", 1, 1, 0, 0, 10, 1, 1920, 1080, 9_000_000)));
        await cache.StoreAsync(DatabasePath, new MediaProbeCacheEntry(withoutBitrate, 1, 1, new MediaInfo("h264", 1, 1, 0, 0, 10, 1, 1920, 1080, null)));
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE media_probe_cache SET cache_schema_version = 1;";
            await command.ExecuteNonQueryAsync();
        }

        (await cache.LoadAllAsync(DatabasePath)).Select(entry => entry.SourcePath).Should().Equal(withBitrate);
    }

    [Fact]
    public async Task ScanAsyncReusesCachedProbeAndReevaluatesCurrentEligibilityRules()
    {
        var path = await WriteVideoAsync("movie.mkv", [1, 2, 3]);
        var probe = Probe();
        var scanner = new FileScanner(new ReadableFiles(), _ => probe, Cache());
        var eligible = Settings();

        var first = await scanner.ScanAsync(eligible.Watch.Roots, eligible);
        var changedRules = Settings(codecs: ["hevc"]);
        var second = await scanner.ScanAsync(changedRules.Watch.Roots, changedRules);

        first.RealProbes.Should().Be(1);
        second.CacheHits.Should().Be(1);
        probe.Calls.Should().Be(1);
        second.Items.Should().ContainSingle(item => item.Path == path && item.Status == ScanItemStatus.Ineligible);
    }

    /// <summary>
    /// A new write time alone sends the file back to ffprobe and replaces its entry, so the cache never describes an
    /// earlier version of the file.
    /// </summary>
    [Fact]
    public async Task ScanAsyncReprobesAndReplacesCacheWhenOnlyLastWriteTimeChanges()
    {
        var path = await WriteVideoAsync("movie.mkv", [1, 2, 3]);
        var probe = Probe();
        var scanner = new FileScanner(new ReadableFiles(), _ => probe, Cache());
        var settings = Settings();

        await scanner.ScanAsync(settings.Watch.Roots, settings);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        var second = await scanner.ScanAsync(settings.Watch.Roots, settings);
        var info = new FileInfo(path);

        second.RealProbes.Should().Be(1);
        probe.Calls.Should().Be(2);
        (await Cache().LoadAllAsync(DatabasePath)).Should().ContainSingle(entry =>
            entry.SourcePath == path && entry.SourceSizeBytes == info.Length && entry.SourceLastWriteUtcTicks == info.LastWriteTimeUtc.Ticks);
    }

    [Fact]
    public async Task ScanAsyncDoesNotCacheFailedOrNotReadySources()
    {
        var failedPath = await WriteVideoAsync("failed.mkv", [1]);
        var settings = Settings();
        await new FileScanner(new ReadableFiles(), _ => new FuncProbe(_ => throw new MediaProbeException("Probe failed.")), Cache()).ScanAsync(settings.Watch.Roots, settings);

        var notReadyPath = await WriteVideoAsync("not-ready.mkv", [2]);
        await new FileScanner(new ReadableFiles(ready: false), _ => Probe(), Cache()).ScanAsync(settings.Watch.Roots, settings);

        (await Cache().LoadAllAsync(DatabasePath)).Select(entry => entry.SourcePath).Should().NotContain([failedPath, notReadyPath]);
    }

    [Fact]
    public async Task ScanAsyncDoesNotCacheOrQueueAFileThatChangesDuringProbing()
    {
        var path = await WriteVideoAsync("changing.mkv", [1, 2, 3]);
        var settings = Settings();
        var changing = new FuncProbe(file =>
        {
            File.AppendAllBytes(file, [4]);
            return new MediaInfo("h264", 1, 0, 0, 0, null, null, 1920, 1080, 10_000_000);
        });

        var report = await new FileScanner(new ReadableFiles(), _ => changing, Cache()).ScanAsync(settings.Watch.Roots, settings);

        report.Items.Should().ContainSingle(item => item.Path == path && item.Status == ScanItemStatus.Unavailable && item.Reason == "Original changed while probing.");
        (await Cache().LoadAllAsync(DatabasePath)).Select(entry => entry.SourcePath).Should().NotContain(path);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");
    private static SqliteMediaProbeCache Cache() => new(new SqliteDatabaseInitializer());

    private static FuncProbe Probe() => new(path => new MediaInfo("h264", 1, 2, 3, 4, 120.5, new FileInfo(path).Length, 1920, 1080, 10_000_000));

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
}
