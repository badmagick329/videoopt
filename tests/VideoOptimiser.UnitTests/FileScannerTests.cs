using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class FileScannerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public FileScannerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ScanAsyncClassifiesAllowedH264AndRejectedCodecs()
    {
        var h264 = Path.Combine(_directory, "h264.mkv");
        var hevc = Path.Combine(_directory, "hevc.mkv");
        await File.WriteAllBytesAsync(h264, [1, 2, 3]);
        await File.WriteAllBytesAsync(hevc, [4, 5, 6]);
        var settings = Settings("1B");

        var report = await Scanner().ScanAsync(settings.Watch.Roots, settings);

        report.Items.Should().ContainSingle(item => item.Path == h264 && item.Status == ScanItemStatus.Eligible);
        report.Items.Should().ContainSingle(item => item.Path == hevc && item.Status == ScanItemStatus.Ineligible && item.Reason.Contains("hevc"));
    }

    /// <summary>The probe gives no duration, so the file size stands in for the stream. Cached and fresh probes are judged alike.</summary>
    [Fact]
    public async Task ScanAsyncKeepsOutAVideoTooSmallToSaveTheMinimum()
    {
        var fresh = Path.Combine(_directory, "fresh.mkv");
        var cached = Path.Combine(_directory, "cached.mkv");
        var bigEnough = Path.Combine(_directory, "big.mkv");
        await File.WriteAllBytesAsync(fresh, [1, 2, 3]);
        await File.WriteAllBytesAsync(cached, [1, 2, 3]);
        await File.WriteAllBytesAsync(bigEnough, [1, 2, 3, 4]);
        var settings = Settings("1B");
        settings.Savings.MinimumBytesSaved = "3B";

        var report = await Scanner(new FakeProbeCache(cached)).ScanAsync(settings.Watch.Roots, settings);

        report.Items.Where(item => item.Status == ScanItemStatus.Ineligible).Select(item => (item.Path, item.Reason)).Should().BeEquivalentTo(new[]
        {
            (fresh, "Too small to save 3B (savings.minimumBytesSaved)."),
            (cached, "Too small to save 3B (savings.minimumBytesSaved).")
        });
        report.Items.Should().ContainSingle(item => item.Path == bigEnough && item.Status == ScanItemStatus.Eligible);
    }

    [Fact]
    public async Task ScanAsyncSkipsExcludedDirectory()
    {
        var excluded = Path.Combine(_directory, "Archive");
        Directory.CreateDirectory(excluded);
        var path = Path.Combine(excluded, "movie.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var settings = Settings("1B", recursive: true);

        var report = await Scanner().ScanAsync(settings.Watch.Roots, settings);

        report.Items.Should().ContainSingle(item => item.Path == path && item.Reason == "File is in an excluded directory.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsyncSkipsTheArchiveDirectoryEvenWhenItIsNotAnExcludedDirectory(bool absolute)
    {
        var archive = Path.Combine(_directory, "Old originals");
        Directory.CreateDirectory(archive);
        var archived = Path.Combine(archive, "movie.mkv");
        var original = Path.Combine(_directory, "movie.mkv");
        await File.WriteAllBytesAsync(archived, [1, 2, 3]);
        await File.WriteAllBytesAsync(original, [1, 2, 3]);
        var settings = Settings("1B", recursive: true);
        settings.Eligibility.ExcludedDirectories = [];
        settings.Original.ArchiveDirectory = absolute ? archive : "Old originals";

        var report = await Scanner().ScanAsync(settings.Watch.Roots, settings);

        report.Items.Should().ContainSingle(item => item.Path == archived && item.Reason == "File is in the archive directory.");
        report.Items.Should().ContainSingle(item => item.Path == original && item.Status == ScanItemStatus.Eligible);
    }

    /// <summary>
    /// The exclude lists are emptied, so only the tool's own rule keeps its files out. In the last row the watch root is the
    /// staging folder itself, so the walk starts inside a folder it would never have entered.
    /// </summary>
    [Theory]
    [InlineData(".video-optimiser", false)]
    [InlineData("immich-staging", false)]
    [InlineData("immich-staging", true)]
    public async Task ScanAsyncNeverTakesAFileTheToolMadeWhateverTheExcludeListsSay(string folder, bool rootIsToolFolder)
    {
        var toolFolder = Path.Combine(_directory, folder);
        Directory.CreateDirectory(Path.Combine(toolFolder, "asset"));
        var ordinary = Path.Combine(_directory, "movie.mkv");
        foreach (var path in new[] { Path.Combine(toolFolder, "clip.mkv"), Path.Combine(toolFolder, "asset", "clip.mkv"), ordinary })
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
        }
        var settings = Settings("1B", recursive: true);
        settings.Eligibility.ExcludedDirectories = [];
        settings.Eligibility.ExcludedNamePatterns = [];
        settings.Immich.StagingDirectory = Path.Combine(_directory, "immich-staging");
        if (rootIsToolFolder) settings.Watch.Roots[0].Path = toolFolder;

        var report = await Scanner().ScanAsync(settings.Watch.Roots, settings);

        report.Items.Select(item => (item.Path, item.Status)).Should().Equal(rootIsToolFolder ? [] : [(ordinary, ScanItemStatus.Eligible)]);
    }

    /// <summary>
    /// The first file the file system lists is open, so it doesn't count: a stop after two takes the next two and never
    /// checks the fourth. The order comes from a full scan, since the test can't choose the order files are listed in.
    /// </summary>
    [Fact]
    public async Task ScanAsyncStopsAfterTheAskedNumberOfEligibleFilesThatAreNotOpen()
    {
        foreach (var name in new[] { "a.mkv", "b.mkv", "c.mkv", "d.mkv" }) await File.WriteAllBytesAsync(Path.Combine(_directory, name), [1, 2, 3]);
        var settings = Settings("1B");
        var order = (await Scanner().ScanAsync(settings.Watch.Roots, settings)).Items.Select(item => item.Path).ToArray();

        var report = await Scanner().ScanAsync(settings.Watch.Roots, settings, stopAfterEligible: 2, openSourcePaths: new HashSet<string>([order[0]], StringComparer.OrdinalIgnoreCase));

        order.Should().HaveCount(4);
        report.Items.Select(item => (item.Path, item.Status)).Should().Equal(order[..3].Select(path => (path, ScanItemStatus.Eligible)));
    }

    [Fact]
    public async Task ScanAsyncLoadsCachedEntriesOnceWithoutReadinessOrProbeCalls()
    {
        var first = Path.Combine(_directory, "cached-1.mkv");
        var second = Path.Combine(_directory, "cached-2.mkv");
        await File.WriteAllBytesAsync(first, [1, 2, 3]);
        await File.WriteAllBytesAsync(second, [1, 2, 3]);
        var readiness = new ReadableFiles();
        var probe = new FuncProbe(_ => Media());
        var cache = new FakeProbeCache(first, second);
        var settings = Settings("1B");

        var report = await new FileScanner(readiness, _ => probe, cache).ScanAsync(settings.Watch.Roots, settings);

        report.EligibleCount.Should().Be(2);
        report.CacheHits.Should().Be(2);
        report.RealProbes.Should().Be(0);
        cache.LoadCalls.Should().Be(1);
        readiness.Calls.Should().Be(0);
        probe.Calls.Should().Be(0);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private AppSettings Settings(string minimumFileSize, bool recursive = false) => new()
    {
        Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
        Savings = new SavingsSettings { MinimumBytesSaved = "0B" },
        Eligibility = new EligibilitySettings
        {
            Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "1Mbps", MinimumFileSize = minimumFileSize }]
        },
        Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory, Recursive = recursive }] }
    };

    /// <summary>A scanner whose files are all ready and whose probe calls a file hevc if its name says so.</summary>
    private static FileScanner Scanner(IMediaProbeCache? cache = null) =>
        new(new ReadableFiles(), _ => new FuncProbe(path => Media(path.EndsWith("hevc.mkv", StringComparison.Ordinal) ? "hevc" : "h264")), cache ?? new FakeProbeCache());

    /// <summary>Holds the given files as already probed, as they are now, and counts how often it is loaded.</summary>
    private sealed class FakeProbeCache(params string[] cachedPaths) : IMediaProbeCache
    {
        public int LoadCalls { get; private set; }

        public Task<IReadOnlyList<MediaProbeCacheEntry>> LoadAllAsync(string databasePath, CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            return Task.FromResult<IReadOnlyList<MediaProbeCacheEntry>>([.. cachedPaths.Select(path =>
            {
                var info = new FileInfo(path);
                return new MediaProbeCacheEntry(path, info.Length, info.LastWriteTimeUtc.Ticks, Media());
            })]);
        }

        public Task StoreAsync(string databasePath, MediaProbeCacheEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static MediaInfo Media(string codec = "h264") => new(codec, 1, 0, 0, 0, null, null, 1920, 1080, 10_000_000);
}
