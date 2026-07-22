using FluentAssertions;
using VideoOptimiser.Infrastructure.Scanning;

namespace VideoOptimiser.UnitTests;

public sealed class FileReadinessServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public FileReadinessServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task CheckAsyncAcceptsAReadableFile()
    {
        var path = Path.Combine(_directory, "film.mkv");
        await File.WriteAllTextAsync(path, "fixture");

        var result = await new FileReadinessService().CheckAsync(path);

        result.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsyncRejectsAMissingFile()
    {
        var result = await new FileReadinessService().CheckAsync(Path.Combine(_directory, "missing.mkv"));

        result.IsReady.Should().BeFalse();
        result.Reason.Should().Contain("no longer exists");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
