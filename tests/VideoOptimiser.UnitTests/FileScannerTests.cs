using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Infrastructure.Scanning;

namespace VideoOptimiser.UnitTests;

public sealed class FileScannerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public FileScannerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ScanAsyncClassifiesAllowedH264AndRejectedCodecsWithoutMutatingFiles()
    {
        var h264 = Path.Combine(_directory, "h264.mkv");
        var hevc = Path.Combine(_directory, "hevc.mkv");
        var tooSmall = Path.Combine(_directory, "small.mp4");
        await File.WriteAllBytesAsync(h264, [1, 2, 3]);
        await File.WriteAllBytesAsync(hevc, [4, 5, 6]);
        await File.WriteAllBytesAsync(tooSmall, [7]);
        var scanner = new FileScanner(new ReadableFileService(), _ => new CodecProbe(), new NoopProbeCache());
        var settings = Settings("2B");

        var report = await scanner.ScanAsync(settings.Watch.Roots, settings);

        report.Items.Should().ContainSingle(item => item.Path == h264 && item.Status == ScanItemStatus.Eligible);
        report.Items.Should().ContainSingle(item => item.Path == hevc && item.Status == ScanItemStatus.Ineligible && item.Reason.Contains("hevc"));
        report.Items.Should().ContainSingle(item => item.Path == tooSmall && item.Status == ScanItemStatus.Ineligible && item.Reason.Contains("size is below 2B"));
        new FileInfo(h264).Length.Should().Be(3);
    }

    [Fact]
    public async Task ScanAsyncSkipsExcludedDirectory()
    {
        var excluded = Path.Combine(_directory, "Archive");
        Directory.CreateDirectory(excluded);
        var path = Path.Combine(excluded, "movie.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var scanner = new FileScanner(new ReadableFileService(), _ => new CodecProbe(), new NoopProbeCache());
        var settings = Settings("1B", recursive: true);

        var report = await scanner.ScanAsync(settings.Watch.Roots, settings);

        report.Items.Should().ContainSingle(item => item.Path == path && item.Reason == "File is in an excluded directory.");
    }

    [Fact]
    public async Task ScanAsyncStopsAfterTheFirstEligibleFileWhenRequested()
    {
        await File.WriteAllBytesAsync(Path.Combine(_directory, "first.mkv"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "second.mkv"), [4, 5, 6]);
        var scanner = new FileScanner(new ReadableFileService(), _ => new CodecProbe(), new NoopProbeCache());

        var report = await scanner.ScanAsync(Settings("1B").Watch.Roots, Settings("1B"), stopAfterFirstEligible: true);

        report.Items.Should().ContainSingle(item => item.Status == ScanItemStatus.Eligible);
    }

    [Fact]
    public async Task ScanAsyncLoadsCachedEntriesOnceWithoutReadinessOrProbeCalls()
    {
        var cachedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < 250; index++)
        {
            var path = Path.Combine(_directory, $"cached-{index:000}.mkv");
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            cachedPaths.Add(Path.GetFullPath(path));
        }

        var progress = new RecordingProgress<ScanProgress>();
        var readiness = new CountingReadiness();
        var probe = new CountingProbe();
        var cache = new SelectiveProbeCache(cachedPaths);
        var scanner = new FileScanner(readiness, _ => probe, cache);

        var report = await scanner.ScanAsync(Settings("1B").Watch.Roots, Settings("1B"), progress: progress);

        report.Items.Should().HaveCount(250);
        report.EligibleCount.Should().Be(250);
        report.CacheHits.Should().Be(250);
        report.RealProbes.Should().Be(0);
        cache.LoadCalls.Should().Be(1);
        readiness.Calls.Should().Be(0);
        probe.Calls.Should().Be(0);
        progress.Values.Should().NotContain(update => update.Stage == "Cache");
    }

    [Fact]
    public async Task ScanAsyncReprobesWhenCachedSizeOrTimestampDoesNotMatch()
    {
        var path = Path.Combine(_directory, "changed.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var info = new FileInfo(path);
        var cache = new SelectiveProbeCache(new HashSet<string>(), new MediaProbeCacheEntry(path, info.Length + 1, info.LastWriteTimeUtc.Ticks - 1, Media()));
        var readiness = new CountingReadiness();
        var probe = new CountingProbe();
        var scanner = new FileScanner(readiness, _ => probe, cache);

        var report = await scanner.ScanAsync(Settings("1B").Watch.Roots, Settings("1B"));

        report.RealProbes.Should().Be(1);
        readiness.Calls.Should().Be(1);
        probe.Calls.Should().Be(1);
        cache.Stored.Should().ContainSingle(entry => entry.SourcePath == path && entry.SourceSizeBytes == info.Length && entry.SourceLastWriteUtcTicks == info.LastWriteTimeUtc.Ticks);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private AppSettings Settings(string minimumFileSize, bool recursive = false) => new()
    {
        Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
        Eligibility = new EligibilitySettings
        {
            Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "1Mbps", MinimumFileSize = minimumFileSize }]
        },
        Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory, Recursive = recursive }] }
    };

    private sealed class ReadableFileService : IFileReadinessService
    {
        public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new FileReadinessResult(true, "Readable."));
    }

    private sealed class CountingReadiness : IFileReadinessService
    {
        public int Calls { get; private set; }
        public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new FileReadinessResult(true, "Readable."));
        }
    }

    private sealed class CodecProbe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(path.EndsWith("hevc.mkv", StringComparison.Ordinal) ? Media("hevc") : Media());
    }

    private sealed class CountingProbe : IMediaProbe
    {
        public int Calls { get; private set; }
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Media());
        }
    }

    private sealed class NoopProbeCache : IMediaProbeCache
    {
        public Task<IReadOnlyList<MediaProbeCacheEntry>> LoadAllAsync(string databasePath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MediaProbeCacheEntry>>([]);
        public Task StoreAsync(string databasePath, MediaProbeCacheEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SelectiveProbeCache(IReadOnlySet<string> cachedPaths, params MediaProbeCacheEntry[] entries) : IMediaProbeCache
    {
        public int LoadCalls { get; private set; }
        public List<MediaProbeCacheEntry> Stored { get; } = [];

        public Task<IReadOnlyList<MediaProbeCacheEntry>> LoadAllAsync(string databasePath, CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            var cached = cachedPaths.Select(path =>
            {
                var info = new FileInfo(path);
                return new MediaProbeCacheEntry(path, info.Length, info.LastWriteTimeUtc.Ticks, Media());
            }).Concat(entries).ToArray();
            return Task.FromResult<IReadOnlyList<MediaProbeCacheEntry>>(cached);
        }

        public Task StoreAsync(string databasePath, MediaProbeCacheEntry entry, CancellationToken cancellationToken = default)
        {
            Stored.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private static MediaInfo Media(string codec = "h264") => new(codec, 1, 0, 0, 0, null, null, 1920, 1080, 10_000_000);
}
