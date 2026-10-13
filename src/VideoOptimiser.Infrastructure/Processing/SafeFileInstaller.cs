using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.Infrastructure.Processing;

/// <summary>
/// Installs a validated AV1. <c>delete</c> and <c>archive</c> move the original aside before the AV1 takes its path and put
/// it back if that fails, so the original's path always holds the original or a complete AV1. <c>keep</c> never moves the
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
        // Read before the original moves or goes; afterwards there is nothing to copy them from.
        var created = File.GetCreationTimeUtc(sourcePath);
        var modified = File.GetLastWriteTimeUtc(sourcePath);
        if (Is(original.Action, OriginalActions.Keep))
        {
            var installed = AvailablePath(directory, $"{stem}.av1", extension);
            File.Move(outputPath, installed);
            EnsureInstalled(installed);
            return Task.FromResult(new FileInstallation(installed, null, CopyTimestamps(installed, created, modified)));
        }

        if (Is(original.Action, OriginalActions.Archive))
        {
            var archiveDirectory = Path.IsPathFullyQualified(original.ArchiveDirectory) ? original.ArchiveDirectory : Path.Combine(directory, original.ArchiveDirectory);
            Directory.CreateDirectory(archiveDirectory);
            var archived = AvailablePath(archiveDirectory, stem, extension);
            Replace(sourcePath, outputPath, archived);
            return Task.FromResult(new FileInstallation(sourcePath, archived, CopyTimestamps(sourcePath, created, modified)));
        }

        var rollback = Path.Combine(directory, $"{stem}.{Guid.NewGuid():N}.original.pending-delete{extension}");
        Replace(sourcePath, outputPath, rollback);
        File.Delete(rollback);
        return Task.FromResult(new FileInstallation(sourcePath, null, CopyTimestamps(sourcePath, created, modified)));
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

    private static void Replace(string sourcePath, string outputPath, string displacedPath)
    {
        try
        {
            File.Move(sourcePath, displacedPath);
            File.Move(outputPath, sourcePath);
            EnsureInstalled(sourcePath);
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
