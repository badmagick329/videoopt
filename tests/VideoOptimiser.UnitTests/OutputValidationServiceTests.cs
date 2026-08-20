using FluentAssertions;
using System.Text.Json;
using VideoOptimiser.Application.Configuration;
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

        result.Failures.Should().Contain("Output duration differs from source.");
    }

    [Fact]
    public async Task ValidateAsyncFailsWhenRelativeAudioOffsetChanges()
    {
        var manifest = await CreateManifestAsync();
        var result = await ValidateAsync(
            manifest,
            new MediaTimeline(1.881, 149.069, 3_577, 4.132),
            new MediaTimeline(0, 147.188, 3_577, 3));

        result.Failures.Should().Contain("Output audio timing differs from source.");
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

        result.Failures.Should().Contain("Output video packet count differs from source.");
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

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static async Task<ValidationReport> ValidateAsync(OutputManifest manifest, MediaTimeline sourceTimeline, MediaTimeline? outputTimeline, Exception? outputError = null)
    {
        var service = new OutputValidationService(
            _ => new StaticMediaProbe(manifest.OutputPath),
            _ => new StaticTimelineProbe(manifest.SourcePath, sourceTimeline, outputTimeline, outputError));
        return await service.ValidateAsync(manifest, new AppSettings
        {
            Savings = new SavingsSettings { MinimumPercentageSaved = 0 },
            Validation = new ValidationSettings { DurationToleranceSeconds = 0.1 }
        });
    }

    private async Task<OutputManifest> CreateManifestAsync()
    {
        var source = Path.Combine(_directory, $"source-{Guid.NewGuid():N}.mkv");
        var output = Path.Combine(_directory, $"output-{Guid.NewGuid():N}.mkv");
        await File.WriteAllBytesAsync(source, new byte[100]);
        await File.WriteAllBytesAsync(output, new byte[50]);
        return new OutputManifest { SourcePath = source, OutputPath = output };
    }

    private sealed class StaticMediaProbe(string outputPath) : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(
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
                    : throw new InvalidDataException("Malformed packet timing.");
    }
}
