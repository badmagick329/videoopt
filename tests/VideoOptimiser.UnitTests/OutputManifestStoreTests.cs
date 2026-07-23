using FluentAssertions;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class OutputManifestStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public OutputManifestStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task SaveAndLoadRoundTripsThroughTheGeneratedJsonContext()
    {
        var outputPath = Path.Combine(_directory, "output.mp4");
        var manifest = new OutputManifest
        {
            SourcePath = "C:\\Videos\\source.mp4",
            SourceFingerprint = "fingerprint",
            OutputPath = outputPath,
            Crf = 44,
            CreatedUtc = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero),
            Validation = new ValidationReport
            {
                Passed = true,
                Failures = ["none"],
                SourceSizeBytes = 2_000,
                OutputSizeBytes = 1_000,
                PercentageSaved = 50,
                ValidatedUtc = new DateTimeOffset(2026, 7, 23, 12, 1, 0, TimeSpan.Zero)
            }
        };

        var store = new OutputManifestStore();
        await store.SaveAsync(manifest);
        var loaded = await store.LoadAsync(outputPath);

        loaded.SourcePath.Should().Be(manifest.SourcePath);
        loaded.Crf.Should().Be(44);
        loaded.Validation!.Passed.Should().BeTrue();
        loaded.Validation.PercentageSaved.Should().Be(50);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
