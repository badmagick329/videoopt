using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Infrastructure.Features.Finalize;

namespace VideoOptimiser.UnitTests.Features.Finalize;

public sealed class SafeFileInstallerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private string _source;
    private readonly string _output;

    public SafeFileInstallerTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, ".video-optimiser"));
        _source = Path.Combine(_directory, "movie.mp4");
        _output = Path.Combine(_directory, ".video-optimiser", "movie.encoding.mp4");
        File.WriteAllText(_source, "original");
        File.WriteAllText(_output, "av1");
    }

    [Fact]
    public async Task DeleteReplacesTheOriginalAndDeletesTheRollbackFile()
    {
        var installation = await Install(OriginalActions.Delete);

        installation.Should().Be(new FileInstallation(_source, null));
        (await File.ReadAllTextAsync(_source)).Should().Be("av1");
        File.Exists(_output).Should().BeFalse();
        Directory.EnumerateFiles(_directory).Should().Equal(_source);
    }

    [Fact]
    public async Task ArchiveMovesTheOriginalBesideItWithoutOverwritingAnEarlierArchive()
    {
        var archive = Path.Combine(_directory, "Archive");
        Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(Path.Combine(archive, "movie.mp4"), "earlier");

        var installation = await Install(OriginalActions.Archive);

        var archived = Path.Combine(archive, "movie (2).mp4");
        installation.Should().Be(new FileInstallation(_source, archived));
        (await File.ReadAllTextAsync(_source)).Should().Be("av1");
        (await File.ReadAllTextAsync(archived)).Should().Be("original");
        (await File.ReadAllTextAsync(Path.Combine(archive, "movie.mp4"))).Should().Be("earlier");
    }

    [Fact]
    public async Task ArchiveToAnAbsoluteDirectoryCreatesIt()
    {
        var archive = Path.Combine(_directory, "elsewhere", "originals");

        var installation = await Install(OriginalActions.Archive, archive);

        installation.ArchivedOriginalPath.Should().Be(Path.Combine(archive, "movie.mp4"));
        (await File.ReadAllTextAsync(installation.ArchivedOriginalPath!)).Should().Be("original");
        (await File.ReadAllTextAsync(_source)).Should().Be("av1");
    }

    [Fact]
    public async Task KeepLeavesTheOriginalAndInstallsTheAv1BesideItUnderAFreeName()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "movie.av1.mp4"), "earlier");

        var installation = await Install(OriginalActions.Keep);

        var installed = Path.Combine(_directory, "movie.av1 (2).mp4");
        installation.Should().Be(new FileInstallation(installed, null));
        (await File.ReadAllTextAsync(_source)).Should().Be("original");
        (await File.ReadAllTextAsync(installed)).Should().Be("av1");
        (await File.ReadAllTextAsync(Path.Combine(_directory, "movie.av1.mp4"))).Should().Be("earlier");
    }

    [Theory]
    [InlineData(OriginalActions.Delete)]
    [InlineData(OriginalActions.Archive)]
    [InlineData(OriginalActions.Keep)]
    public async Task TheInstalledAv1TakesTheOriginalsFileDates(string action)
    {
        UseOriginalDates();

        var installation = await Install(action);

        installation.Warning.Should().BeNull();
        var installed = new FileInfo(installation.InstalledPath);
        installed.LastWriteTimeUtc.Should().Be(OriginalModified);
        // Creation time is only settable on Windows.
        if (OperatingSystem.IsWindows()) installed.CreationTimeUtc.Should().Be(OriginalCreated);
        (await File.ReadAllTextAsync(installed.FullName)).Should().Be("av1");
    }

    [Fact]
    public async Task ArchiveKeepsTheArchivedOriginalsOwnFileDates()
    {
        UseOriginalDates();

        var installation = await Install(OriginalActions.Archive);

        var archived = new FileInfo(installation.ArchivedOriginalPath!);
        archived.LastWriteTimeUtc.Should().Be(OriginalModified);
        if (OperatingSystem.IsWindows()) archived.CreationTimeUtc.Should().Be(OriginalCreated);
    }

    [Theory]
    [InlineData(OriginalActions.Delete)]
    [InlineData(OriginalActions.Archive)]
    [InlineData(OriginalActions.Keep)]
    public async Task AFileDateFailureLeavesTheAv1InstalledAndReportsAWarning(string action)
    {
        var installer = new SafeFileInstaller((_, _, _) => throw new UnauthorizedAccessException("access denied"));

        var installation = await installer.InstallAsync(_source, _output, new OriginalSettings { Action = action, ArchiveDirectory = "Archive" });

        installation.Warning.Should().Contain("file dates").And.Contain("access denied");
        (await File.ReadAllTextAsync(installation.InstalledPath)).Should().Be("av1");
        File.Exists(_output).Should().BeFalse();
        if (action != OriginalActions.Keep) File.Exists(installation.ArchivedOriginalPath ?? _source).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteInstallsAMovOriginalsAv1AsMp4AndRemovesTheMov()
    {
        var mov = UseMovSource();

        var installation = await Install(OriginalActions.Delete);

        var installed = Path.Combine(_directory, "movie.mp4");
        installation.Should().Be(new FileInstallation(installed, null));
        (await File.ReadAllTextAsync(installed)).Should().Be("av1");
        File.Exists(mov).Should().BeFalse();
        File.Exists(_output).Should().BeFalse();
        Directory.EnumerateFiles(_directory).Should().Equal(installed);
    }

    [Fact]
    public async Task DeleteNumbersTheMp4WhenADifferentFileAlreadyHoldsTheName()
    {
        var mov = UseMovSource();
        var existing = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(existing, "someone else's");

        var installation = await Install(OriginalActions.Delete);

        var installed = Path.Combine(_directory, "movie (2).mp4");
        installation.InstalledPath.Should().Be(installed);
        (await File.ReadAllTextAsync(installed)).Should().Be("av1");
        (await File.ReadAllTextAsync(existing)).Should().Be("someone else's");
        File.Exists(mov).Should().BeFalse();
    }

    [Fact]
    public async Task ArchiveKeepsTheMovArchivedUnderItsOwnExtensionAndInstallsTheMp4()
    {
        var mov = UseMovSource();

        var installation = await Install(OriginalActions.Archive);

        var archived = Path.Combine(_directory, "Archive", "movie.mov");
        var installed = Path.Combine(_directory, "movie.mp4");
        installation.Should().Be(new FileInstallation(installed, archived));
        (await File.ReadAllTextAsync(archived)).Should().Be("original");
        (await File.ReadAllTextAsync(installed)).Should().Be("av1");
        File.Exists(mov).Should().BeFalse();
    }

    [Fact]
    public async Task KeepInstallsTheAv1BesideAMovUnderTheOutputsExtension()
    {
        var mov = UseMovSource();

        var installation = await Install(OriginalActions.Keep);

        var installed = Path.Combine(_directory, "movie.av1.mp4");
        installation.Should().Be(new FileInstallation(installed, null));
        (await File.ReadAllTextAsync(mov)).Should().Be("original");
        (await File.ReadAllTextAsync(installed)).Should().Be("av1");
    }

    [Theory]
    [InlineData(OriginalActions.Delete)]
    [InlineData(OriginalActions.Archive)]
    public async Task AFailedInstallPutsAMovOriginalBackAtItsOwnPath(string action)
    {
        var mov = UseMovSource();
        File.Delete(_output);

        var install = () => Install(action);

        await install.Should().ThrowAsync<FileNotFoundException>();
        (await File.ReadAllTextAsync(mov)).Should().Be("original");
        Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories).Should().Equal(mov);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>Makes the original a .mov, whose AV1 is the .mp4 in <c>_output</c>; the default source is already an .mp4.</summary>
    private string UseMovSource()
    {
        var mov = Path.Combine(_directory, "movie.mov");
        File.Move(_source, mov);
        _source = mov;
        return mov;
    }

    private static readonly DateTime OriginalCreated = new(2021, 3, 4, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OriginalModified = new(2022, 5, 6, 11, 30, 0, DateTimeKind.Utc);

    // The output keeps the dates it was just written with, as a real encode's would be.
    private void UseOriginalDates()
    {
        File.SetCreationTimeUtc(_source, OriginalCreated);
        File.SetLastWriteTimeUtc(_source, OriginalModified);
    }

    private Task<FileInstallation> Install(string action, string archiveDirectory = "Archive") =>
        new SafeFileInstaller().InstallAsync(_source, _output, new OriginalSettings { Action = action, ArchiveDirectory = archiveDirectory });
}
