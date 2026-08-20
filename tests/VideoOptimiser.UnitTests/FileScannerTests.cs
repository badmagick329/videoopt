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
        var settings = new AppSettings
        {
            Eligibility = Rules("2B"),
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] }
        };

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
        var settings = new AppSettings
        {
            Eligibility = Rules("1B"),
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory, Recursive = true }] }
        };

        var report = await scanner.ScanAsync(settings.Watch.Roots, settings);

        report.Items.Should().ContainSingle(item => item.Path == path && item.Reason == "File is in an excluded directory.");
    }

    [Fact]
    public async Task ScanAsyncStopsAfterTheFirstEligibleFileWhenRequested()
    {
        await File.WriteAllBytesAsync(Path.Combine(_directory, "first.mkv"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "second.mkv"), [4, 5, 6]);
        var scanner = new FileScanner(new ReadableFileService(), _ => new CodecProbe(), new NoopProbeCache());
        var settings = new AppSettings
        {
            Eligibility = Rules("1B"),
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] }
        };

        var report = await scanner.ScanAsync(settings.Watch.Roots, settings, stopAfterFirstEligible: true);

        report.Items.Should().ContainSingle(item => item.Status == ScanItemStatus.Eligible);
    }

    [Fact]
    public async Task ScanAsyncReportsCacheMilestonesWithoutPerFileNoiseAndKeepsProbeFeedback()
    {
        var cachedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < 250; index++)
        {
            var path = Path.Combine(_directory, $"cached-{index:000}.mkv");
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            cachedPaths.Add(Path.GetFullPath(path));
        }

        var probedPath = Path.Combine(_directory, "probe.mkv");
        await File.WriteAllBytesAsync(probedPath, [4, 5, 6]);
        var progress = new RecordingProgress<ScanProgress>();
        var scanner = new FileScanner(new ReadableFileService(), _ => new CodecProbe(), new SelectiveProbeCache(cachedPaths));
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Eligibility = Rules("1B"),
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] }
        };

        var report = await scanner.ScanAsync(settings.Watch.Roots, settings, progress: progress);

        report.Items.Should().HaveCount(251);
        report.EligibleCount.Should().Be(251);
        report.CacheHits.Should().Be(250);
        report.RealProbes.Should().Be(1);
        progress.Values.Should().NotContain(update => update.Stage == "Readiness");
        var cacheUpdates = progress.Values.Where(update => update.Stage == "Cache").ToArray();
        cacheUpdates.Should().HaveCount(2);
        cacheUpdates.Should().OnlyContain(update => string.IsNullOrEmpty(update.Path));
        cacheUpdates.Select(update => update.Message).Should().Equal(
            "Reused cached ffprobe metadata for 100 files.",
            "Reused cached ffprobe metadata for 200 files.");
        progress.Values.Should().Contain(update => update.Stage == "Probing" && update.Path == Path.GetFullPath(probedPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ReadableFileService : IFileReadinessService
    {
        public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FileReadinessResult(true, "Readable."));
    }

    private sealed class CodecProbe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaInfo(path.EndsWith("hevc.mkv", StringComparison.Ordinal) ? "hevc" : "h264", 1, 0, 0, 0, null, null, 1920, 1080, 10_000_000));
    }

    private sealed class NoopProbeCache : IMediaProbeCache
    {
        public Task<MediaInfo?> GetAsync(string databasePath, string sourcePath, long sourceSizeBytes, long sourceLastWriteUtcTicks, CancellationToken cancellationToken = default) => Task.FromResult<MediaInfo?>(null);
        public Task StoreAsync(string databasePath, MediaProbeCacheEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SelectiveProbeCache(IReadOnlySet<string> cachedPaths) : IMediaProbeCache
    {
        public Task<MediaInfo?> GetAsync(string databasePath, string sourcePath, long sourceSizeBytes, long sourceLastWriteUtcTicks, CancellationToken cancellationToken = default) =>
            Task.FromResult<MediaInfo?>(cachedPaths.Contains(Path.GetFullPath(sourcePath))
                ? new MediaInfo("h264", 1, 0, 0, 0, null, sourceSizeBytes, 1920, 1080, 10_000_000)
                : null);

        public Task StoreAsync(string databasePath, MediaProbeCacheEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private static EligibilitySettings Rules(string minimumFileSize) => new EligibilitySettings
    {
        Rules = new List<EligibilityRuleSettings> { new() { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "1Mbps", MinimumFileSize = minimumFileSize } }
    };
}
