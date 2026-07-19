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

        var scanner = new FileScanner(new StableFileService(), _ => new CodecProbe());
        var settings = new AppSettings
        {
            Processing = new ProcessingSettings { MinimumFileSize = "2B" },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] }
        };

        var report = await scanner.ScanAsync(settings.Watch.Roots, settings);

        report.Items.Should().ContainSingle(item => item.Path == h264 && item.Status == ScanItemStatus.Eligible);
        report.Items.Should().ContainSingle(item => item.Path == hevc && item.Status == ScanItemStatus.Ineligible && item.Reason.Contains("hevc"));
        report.Items.Should().ContainSingle(item => item.Path == tooSmall && item.Status == ScanItemStatus.Ineligible && item.Reason.Contains("minimumFileSize"));
        new FileInfo(h264).Length.Should().Be(3);
    }

    [Fact]
    public async Task ScanAsyncSkipsExcludedDirectory()
    {
        var excluded = Path.Combine(_directory, "Archive");
        Directory.CreateDirectory(excluded);
        var path = Path.Combine(excluded, "movie.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var scanner = new FileScanner(new StableFileService(), _ => new CodecProbe());
        var settings = new AppSettings
        {
            Processing = new ProcessingSettings { MinimumFileSize = "1B" },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory, Recursive = true }] }
        };

        var report = await scanner.ScanAsync(settings.Watch.Roots, settings);

        report.Items.Should().ContainSingle(item => item.Path == path && item.Reason == "File is in an excluded directory.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class StableFileService : IFileStabilityService
    {
        public Task<StabilityResult> WaitUntilStableAsync(string path, StabilitySettings settings, bool requireRepeatedObservations = true, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StabilityResult(true, "Stable."));
    }

    private sealed class CodecProbe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaInfo(path.EndsWith("hevc.mkv", StringComparison.Ordinal) ? "hevc" : "h264", 1, 0, 0, 0, null, null));
    }
}
