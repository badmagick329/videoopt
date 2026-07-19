using FluentAssertions;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class SafeFileInstallerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    public SafeFileInstallerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task InstallAsyncReplacesSourceAndDeletesRollback()
    {
        var source = Path.Combine(_directory, "movie.mp4");
        var output = Path.Combine(_directory, "movie.encoding.mp4");
        await File.WriteAllTextAsync(source, "original");
        await File.WriteAllTextAsync(output, "av1");

        await new SafeFileInstaller().InstallAsync(source, output);

        (await File.ReadAllTextAsync(source)).Should().Be("av1");
        File.Exists(output).Should().BeFalse();
        Directory.EnumerateFiles(_directory, "*.original.pending-delete.mp4").Should().BeEmpty();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
