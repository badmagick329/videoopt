using FluentAssertions;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Infrastructure.Eligibility;

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

    /// <summary>
    /// A file another program is still writing is not ready. The check used to allow write sharing, so it passed against a
    /// writer that lets others read, as copy tools do (VOPT-169). Once the writer closes, the file is ready.
    /// </summary>
    [Fact]
    public async Task CheckAsyncRejectsAFileOpenForWriting()
    {
        var path = Path.Combine(_directory, "film.mkv");
        await File.WriteAllTextAsync(path, "fixture");
        var service = new FileReadinessService();

        FileReadinessResult whileWriting;
        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            whileWriting = await service.CheckAsync(path);
        }

        whileWriting.Should().Be(new FileReadinessResult(false, "File is open in another program, which may still be writing it."));
        (await service.CheckAsync(path)).IsReady.Should().BeTrue();
    }

    public void Dispose()=> Directory.Delete(_directory, recursive: true);
}
