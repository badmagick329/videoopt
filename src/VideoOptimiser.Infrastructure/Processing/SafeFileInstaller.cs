using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.Infrastructure.Processing;

/// <summary>
/// Installs a validated AV1 under the output's extension, which can differ from the original's (a .mov original gets a .mp4).
/// <c>delete</c> and <c>archive</c> move the original aside before the AV1 takes the original's name and put it back if that
/// fails, so the original's path always holds the original, or the AV1 is complete at its installed path. When the extension
/// changes the AV1 never overwrites a different file already at its name; it is numbered instead. <c>keep</c> never moves the
/// original and installs the AV1 next to it. The AV1 then takes the original's created and modified dates, so it does not look
/// newly made; failing to set them only warns, because the AV1 is already safely in place.
/// </summary>
public sealed class SafeFileInstaller : ISafeFileInstaller
{
    private readonly Action<string, DateTime, DateTime> _setTimestamps;

    public SafeFileInstaller() : this(SetTimestamps)
    {
    }

    // The seam lets a test force the failure, which a real file system makes hard to provoke.
    internal SafeFileInstaller(Action<string, DateTime, DateTime> setTimestamps) => _setTimestamps = setTimestamps;

    public Task<FileInstallation> InstallAsync(string sourcePath, string outputPath, OriginalSettings original, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(sourcePath)!;
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        var outputExtension = Path.GetExtension(outputPath);
        // Read before the original moves or goes; afterwards there is nothing to copy them from.
        var created = File.GetCreationTimeUtc(sourcePath);
        var modified = File.GetLastWriteTimeUtc(sourcePath);
        if (Is(original.Action, OriginalActions.Keep))
        {
            var installed = AvailablePath(directory, $"{stem}.av1", outputExtension);
            File.Move(outputPath, installed);
            EnsureInstalled(installed);
            return Task.FromResult(new FileInstallation(installed, null, CopyTimestamps(installed, created, modified)));
        }

        if (Is(original.Action, OriginalActions.Archive))
        {
            var archiveDirectory = Path.IsPathFullyQualified(original.ArchiveDirectory) ? original.ArchiveDirectory : Path.Combine(directory, original.ArchiveDirectory);
            Directory.CreateDirectory(archiveDirectory);
            var archived = AvailablePath(archiveDirectory, stem, extension);
            var installedPath = InstalledPath(sourcePath, directory, stem, extension, outputExtension);
            Replace(sourcePath, outputPath, archived, installedPath);
            return Task.FromResult(new FileInstallation(installedPath, archived, CopyTimestamps(installedPath, created, modified)));
        }

        var rollback = Path.Combine(directory, $"{stem}.{Guid.NewGuid():N}.original.pending-delete{extension}");
        var deletedInstalledPath = InstalledPath(sourcePath, directory, stem, extension, outputExtension);
        Replace(sourcePath, outputPath, rollback, deletedInstalledPath);
        File.Delete(rollback);
        return Task.FromResult(new FileInstallation(deletedInstalledPath, null, CopyTimestamps(deletedInstalledPath, created, modified)));
    }

    private string? CopyTimestamps(string installedPath, DateTime created, DateTime modified)
    {
        try
        {
            _setTimestamps(installedPath, created, modified);
            return null;
        }
        catch (Exception exception)
        {
            return $"the AV1 is installed, but the original's file dates could not be copied to it ({exception.Message})";
        }
    }

    // Creation time is only settable on Windows; elsewhere the modified time is the date that carries over.
    private static void SetTimestamps(string path, DateTime created, DateTime modified)
    {
        if (OperatingSystem.IsWindows()) File.SetCreationTimeUtc(path, created);
        File.SetLastWriteTimeUtc(path, modified);
    }

    /// <summary>
    /// The original's own path when the extension is unchanged, as before. Otherwise the AV1 takes the original's name under
    /// the new extension; it is looked up before the original moves, so the original itself never counts as a collision.
    /// </summary>
    private static string InstalledPath(string sourcePath, string directory, string stem, string extension, string outputExtension) =>
        extension.Equals(outputExtension, StringComparison.OrdinalIgnoreCase) ? sourcePath : AvailablePath(directory, stem, outputExtension);

    private static void Replace(string sourcePath, string outputPath, string displacedPath, string installedPath)
    {
        try
        {
            File.Move(sourcePath, displacedPath);
            File.Move(outputPath, installedPath);
            EnsureInstalled(installedPath);
        }
        catch
        {
            if (!File.Exists(sourcePath) && File.Exists(displacedPath)) File.Move(displacedPath, sourcePath);
            throw;
        }
    }

    private static void EnsureInstalled(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException("Installed output is missing.");
    }

    // Numbers the name like Explorer does, so an existing file is never overwritten.
    private static string AvailablePath(string directory, string stem, string extension)
    {
        var path = Path.Combine(directory, stem + extension);
        for (var number = 2; File.Exists(path) || Directory.Exists(path); number++)
        {
            path = Path.Combine(directory, $"{stem} ({number}){extension}");
        }

        return path;
    }

    private static bool Is(string action, string expected) => action.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
