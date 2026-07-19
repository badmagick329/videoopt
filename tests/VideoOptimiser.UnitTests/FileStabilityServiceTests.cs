using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Infrastructure.Scanning;

namespace VideoOptimiser.UnitTests;

public sealed class FileStabilityServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public FileStabilityServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task WaitUntilStableAsyncAcceptsReadableOldFileAfterRequiredObservations()
    {
        var path = Path.Combine(_directory, "film.mkv");
        await File.WriteAllTextAsync(path, "fixture");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var settings = new StabilitySettings
        {
            PollInterval = "1ms",
            RequiredStableChecks = 2,
            MinimumAge = "0ms",
            Timeout = "1s"
        };

        var result = await new FileStabilityService().WaitUntilStableAsync(path, settings);

        result.IsStable.Should().BeTrue();
    }

    [Fact]
    public async Task WaitUntilStableAsyncDefersNewFiles()
    {
        var path = Path.Combine(_directory, "new.mkv");
        await File.WriteAllTextAsync(path, "fixture");
        var settings = new StabilitySettings { MinimumAge = "1h" };

        var result = await new FileStabilityService().WaitUntilStableAsync(path, settings);

        result.IsStable.Should().BeFalse();
        result.Reason.Should().Contain("minimum age");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
