using FluentAssertions;
using System.Text.Json;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class OutputValidationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public OutputValidationServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ValidateAsyncPassesUniformTimelineRebasing()
    {
        var manifest = await CreateManifestAsync();
        var result = await ValidateAsync(
            manifest,
            new MediaTimeline(1.881, 149.069, 3_577, 4.132),
            new MediaTimeline(0, 147.188, 3_577, 2.251));

        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsyncFailsWhenPrimaryVideoTimelineIsShorter()
    {
        var manifest = await CreateManifestAsync();
        var result = await ValidateAsync(
            manifest,
            new MediaTimeline(0, 149.069, 3_577, 2.251),
            new MediaTimeline(0, 145, 3_577, 2.251));

        result.Failures.Should().Contain("Output duration differs from the original.");
    }

    [Fact]
    public async Task ValidateAsyncFailsWhenRelativeAudioOffsetChanges()
    {
        var manifest = await CreateManifestAsync();
        var result = await ValidateAsync(
            manifest,
            new MediaTimeline(1.881, 149.069, 3_577, 4.132),
            new MediaTimeline(0, 147.188, 3_577, 3));

        result.Failures.Should().Contain("Output audio timing differs from the original.");
    }

    [Fact]
    public async Task ValidateAsyncPassesWhenOnePrimaryVideoPacketDiffersWithinDurationTolerance()
    {
        var manifest = await CreateManifestAsync();
        var result = await ValidateAsync(
            manifest,
            new MediaTimeline(0, 149.069, 3_577, 2.251),
            new MediaTimeline(0, 149.069, 3_576, 2.251));

        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsyncFailsWhenPrimaryVideoPacketCountDifferenceExceedsDurationTolerance()
    {
        var manifest = await CreateManifestAsync();
        var result = await ValidateAsync(
            manifest,
            new MediaTimeline(0, 149.069, 3_577, 2.251),
            new MediaTimeline(0, 149.069, 3_570, 2.251));

        result.Failures.Should().Contain("Output video packet count differs from the original.");
    }

    [Fact]
    public async Task ValidateAsyncPassesEqualZeroBasedTimelines()
    {
        var manifest = await CreateManifestAsync();
        var result = await ValidateAsync(
            manifest,
            new MediaTimeline(0, 149.069, 3_577, 2.251),
            new MediaTimeline(0, 149.069, 3_577, 2.251));

        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsyncFailsSafelyWhenPacketTimingCannotBeRead()
    {
        var manifest = await CreateManifestAsync();
        var result = await ValidateAsync(
            manifest,
            new MediaTimeline(0, 149.069, 3_577, 2.251),
            null);

        result.Passed.Should().BeFalse();
        result.Failures.Should().Contain("Output media timeline could not be validated.");
    }

    [Fact]
    public async Task ValidateAsyncFailsSafelyWhenStreamMetadataIsMalformed()
    {
        var manifest = await CreateManifestAsync();
        var result = await ValidateAsync(
            manifest,
            new MediaTimeline(0, 149.069, 3_577, 2.251),
            null,
            new JsonException("Malformed stream metadata."));

        result.Passed.Should().BeFalse();
        result.Failures.Should().Contain("Output media timeline could not be validated.");
    }

    [Fact]
    public async Task ValidateAsyncFailsWhenBytesSavedAreBelowMinimum()
    {
        var manifest = await CreateManifestAsync();
        var result = await ValidateAsync(
            manifest,
            new MediaTimeline(0, 149.069, 3_577, 2.251),
            new MediaTimeline(0, 149.069, 3_577, 2.251),
            minimumBytesSaved: "51B");

        result.Passed.Should().BeFalse();
        result.Failures.Should().Equal("Output saves less than 51B.");
    }

    [Fact]
    public async Task ValidateAsyncFailsWhenFfprobeCannotReadTheOutput()
    {
        var manifest = await CreateManifestAsync();
        var timeline = new MediaTimeline(0, 149.069, 3_577, 2.251);
        var decodeTester = new RecordingDecodeTester(new DecodeTestResult(true, null));
        var result = await ValidateAsync(
            manifest,
            timeline,
            timeline,
            outputProbeError: new MediaProbeException("ffprobe exited with 1: [mov,mp4,m4a,3gp,3g2,mj2 @ 0000] moov atom not found"),
            decodeTester: decodeTester);

        result.Passed.Should().BeFalse();
        result.Failures.Should().Equal($"Output '{Path.GetFileName(manifest.OutputPath)}' could not be read by ffprobe: ffprobe exited with 1: [mov,mp4,m4a,3gp,3g2,mj2 @ 0000] moov atom not found");
        result.OutputSizeBytes.Should().Be(50);
        decodeTester.Windows.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsyncKeepsAnUnreadableOriginalAnErrorRatherThanAVerdictOnTheOutput()
    {
        var manifest = await CreateManifestAsync();
        var timeline = new MediaTimeline(0, 149.069, 3_577, 2.251);

        var action = () => ValidateAsync(manifest, timeline, timeline, sourceProbeError: new MediaProbeException("ffprobe exited with 1: Invalid data found"));

        (await action.Should().ThrowAsync<MediaProbeException>()).WithMessage($"Original '{Path.GetFileName(manifest.SourcePath)}' could not be read by ffprobe: *");
    }

    [Fact]
    public async Task ValidateAsyncFailsWhenOutputIsEmpty()
    {
        var manifest = await CreateManifestAsync(outputBytes: 0);
        var result = await ValidateAsync(manifest, new MediaTimeline(0, 149.069, 3_577, 2.251), null);

        result.Passed.Should().BeFalse();
        result.Failures.Should().Equal("Output is empty.");
    }

    [Fact]
    public async Task ValidateAsyncFailsWhenOutputDoesNotDecode()
    {
        var manifest = await CreateManifestAsync();
        var decodeTester = new RecordingDecodeTester(new DecodeTestResult(false, "Error submitting packet to decoder: Invalid data found"));
        var result = await ValidateAsync(
            manifest,
            new MediaTimeline(0, 149.069, 3_577, 2.251),
            new MediaTimeline(0, 149.069, 3_577, 2.251),
            decodeTester: decodeTester);

        result.Passed.Should().BeFalse();
        result.Failures.Should().Equal("Output failed the decode test: Error submitting packet to decoder: Invalid data found");
        decodeTester.Windows.Should().ContainSingle();
    }

    [Fact]
    public async Task ValidateAsyncSkipsDecodeTestWhenAnotherCheckFails()
    {
        var manifest = await CreateManifestAsync();
        var decodeTester = new RecordingDecodeTester(new DecodeTestResult(true, null));
        var result = await ValidateAsync(
            manifest,
            new MediaTimeline(0, 149.069, 3_577, 2.251),
            new MediaTimeline(0, 140, 3_360, 2.251),
            decodeTester: decodeTester);

        result.Passed.Should().BeFalse();
        decodeTester.Windows.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsyncDecodesStartMiddleAndTailOfLongOutput()
    {
        var manifest = await CreateManifestAsync();
        var decodeTester = new RecordingDecodeTester(new DecodeTestResult(true, null));
        var result = await ValidateAsync(
            manifest,
            new MediaTimeline(0, 149.069, 3_577, 2.251),
            new MediaTimeline(0, 149.069, 3_577, 2.251),
            decodeTester: decodeTester);

        result.Passed.Should().BeTrue();
        decodeTester.Windows.Should().Equal(
            new DecodeWindow(0, 10),
            new DecodeWindow((147.189 - 10) / 2, 10),
            new DecodeWindow(147.189 - 10, null));
    }

    [Theory]
    [InlineData("none", 600d, 0)]
    [InlineData("full", 600d, 1)]
    [InlineData("sampled", 30d, 1)]
    [InlineData("sampled", null, 1)]
    public void SelectDecodeWindowsDecodesWholeOutputUnlessSamplingApplies(string mode, double? duration, int expectedWindows)
    {
        var windows = OutputValidationService.SelectDecodeWindows(mode, duration);

        windows.Should().Equal(Enumerable.Repeat(DecodeWindow.Whole, expectedWindows));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static async Task<ValidationReport> ValidateAsync(
        OutputManifest manifest,
        MediaTimeline sourceTimeline,
        MediaTimeline? outputTimeline,
        Exception? outputError = null,
        string minimumBytesSaved = "0B",
        RecordingDecodeTester? decodeTester = null,
        Exception? outputProbeError = null,
        Exception? sourceProbeError = null)
    {
        decodeTester ??= new RecordingDecodeTester(new DecodeTestResult(true, null));
        var service = new OutputValidationService(
            _ => new StaticMediaProbe(manifest.OutputPath, outputProbeError, sourceProbeError),
            _ => new StaticTimelineProbe(manifest.SourcePath, sourceTimeline, outputTimeline, outputError),
            _ => decodeTester);
        return await service.ValidateAsync(manifest, new AppSettings
        {
            Savings = new SavingsSettings { MinimumPercentageSaved = 0, MinimumBytesSaved = minimumBytesSaved },
            Validation = new ValidationSettings { DurationToleranceSeconds = 0.1 }
        });
    }

    private async Task<OutputManifest> CreateManifestAsync(int outputBytes = 50)
    {
        var source = Path.Combine(_directory, $"source-{Guid.NewGuid():N}.mkv");
        var output = Path.Combine(_directory, $"output-{Guid.NewGuid():N}.mkv");
        await File.WriteAllBytesAsync(source, new byte[100]);
        await File.WriteAllBytesAsync(output, new byte[outputBytes]);
        return new OutputManifest { SourcePath = source, OutputPath = output };
    }

    private sealed class RecordingDecodeTester(DecodeTestResult result) : IMediaDecodeTester
    {
        public List<DecodeWindow> Windows { get; } = [];

        public Task<DecodeTestResult> DecodeAsync(string path, DecodeWindow window, CancellationToken cancellationToken = default)
        {
            Windows.Add(window);
            return Task.FromResult(result);
        }
    }

    private sealed class StaticMediaProbe(string outputPath, Exception? outputError = null, Exception? sourceError = null) : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            (path == outputPath ? outputError : sourceError) is { } error ? Task.FromException<MediaInfo>(error) : Task.FromResult(
            new MediaInfo(
                path == outputPath ? "av1" : "h264",
                1,
                1,
                0,
                0,
                path == outputPath ? 147.189 : 149.069,
                null,
                1920,
                1080,
                10_000_000));
    }

    private sealed class StaticTimelineProbe(string sourcePath, MediaTimeline sourceTimeline, MediaTimeline? outputTimeline, Exception? outputError) : IMediaTimelineProbe
    {
        public Task<MediaTimeline> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            path == sourcePath
                ? Task.FromResult(sourceTimeline)
                : outputError is not null
                    ? Task.FromException<MediaTimeline>(outputError)
                : outputTimeline is { } timeline
                    ? Task.FromResult(timeline)
                    : throw new MediaProbeException("Malformed packet timing.");
    }
}
