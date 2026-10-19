using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Finalize;

namespace VideoOptimiser.Infrastructure.Features.Finalize;

/// <summary>
/// Installs a validated AV1 under the output's extension, which can differ from the original's (a .mov original gets a .mp4).
/// <c>delete</c> and <c>archive</c> move the original aside before the AV1 takes the original's name and put it back if that
/// fails, so the original's path always holds the original, or the AV1 is complete at its installed path. When the extension
/// changes the AV1 never overwrites a different file already at its name; it is numbered instead. <c>keep</c> never moves the
/// original and installs the AV1 next to it. The AV1 then takes the original's created and modified dates, so it does not look
/// newly made; failing to set them, or to delete the original afterwards, only warns, because the AV1 is already safely in
/// place. A move that fails with both files back where they started throws <see cref="FileInstallBlockedException"/>.
/// <see cref="Plan"/> works out every path first and moves nothing, so the caller can record where the original goes before
/// <see cref="InstallAsync"/> moves it: a crash between the two moves would otherwise leave it there with nothing naming it.
/// </summary>
public sealed class SafeFileInstaller : ISafeFileInstaller
{
    private readonly Action<string, DateTime, DateTime> _setTimestamps;
    private readonly Action<string> _deleteFile;

    public SafeFileInstaller() : this(SetTimestamps, File.Delete)
    {
    }

    // The seams let a test force the failures, which a real file system makes hard to provoke.
    internal SafeFileInstaller(Action<string, DateTime, DateTime> setTimestamps, Action<string>? deleteFile = null)
    {
        _setTimestamps = setTimestamps;
        _deleteFile = deleteFile ?? File.Delete;
    }

    public FileInstallPlan Plan(string sourcePath, string outputPath, OriginalSettings original)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        var outputExtension = Path.GetExtension(outputPath);
        if (Is(original.Action, OriginalActions.Keep))
        {
            return new FileInstallPlan(sourcePath, outputPath, OriginalActions.Keep, AvailablePath(directory, $"{stem}.av1", outputExtension), null);
        }

        var installedPath = InstalledPath(sourcePath, directory, stem, extension, outputExtension);
        if (Is(original.Action, OriginalActions.Archive))
        {
            // The archive directory is created by the install; a path in a folder that does not exist yet is free all the same.
            var archiveDirectory = Path.IsPathFullyQualified(original.ArchiveDirectory) ? original.ArchiveDirectory : Path.Combine(directory, original.ArchiveDirectory);
            return new FileInstallPlan(sourcePath, outputPath, OriginalActions.Archive, installedPath, AvailablePath(archiveDirectory, stem, extension));
        }

        var rollback = Path.Combine(directory, $"{stem}.{Guid.NewGuid():N}.original.pending-delete{extension}");
        return new FileInstallPlan(sourcePath, outputPath, OriginalActions.Delete, installedPath, rollback);
    }

    public Task<FileInstallation> InstallAsync(FileInstallPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (sourcePath, outputPath) = (plan.SourcePath, plan.OutputPath);
        // Read before the original moves or goes; afterwards there is nothing to copy them from.
        var created = File.GetCreationTimeUtc(sourcePath);
        var modified = File.GetLastWriteTimeUtc(sourcePath);
        if (plan.DisplacedPath is not { } displaced)
        {
            try
            {
                File.Move(outputPath, plan.InstalledPath);
            }
            catch (Exception exception) when (BlockedIfNothingMoved(sourcePath, outputPath, outputPath, isOriginal: false, exception) is { } blocked)
            {
                throw blocked;
            }
            EnsureInstalled(plan.InstalledPath);
            return Task.FromResult(new FileInstallation(plan.InstalledPath, null, CopyTimestamps(plan.InstalledPath, created, modified)));
        }

        if (Is(plan.Action, OriginalActions.Archive))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(displaced)!);
            Replace(sourcePath, outputPath, displaced, plan.InstalledPath);
            return Task.FromResult(new FileInstallation(plan.InstalledPath, displaced, CopyTimestamps(plan.InstalledPath, created, modified)));
        }

        Replace(sourcePath, outputPath, displaced, plan.InstalledPath);
        // The AV1 is already in place, so a failed delete only leaves the original behind; failing the job here would record
        // a finalisation that worked as one that didn't (VOPT-143).
        string? deleteWarning = null;
        try
        {
            _deleteFile(displaced);
        }
        catch (Exception exception)
        {
            deleteWarning = $"the original could not be deleted ({exception.Message}), so it is still at {displaced}; delete it once you have checked the AV1";
        }
        var warning = string.Join(". Also, ", new[] { deleteWarning, CopyTimestamps(plan.InstalledPath, created, modified) }.OfType<string>());
        return Task.FromResult(new FileInstallation(plan.InstalledPath, null, warning.Length == 0 ? null : warning, deleteWarning is null ? null : displaced));
    }

    /// <summary>
    /// A failed move becomes <see cref="FileInstallBlockedException"/> only when both files are where they started, so the
    /// job can go back to ready; anything less certain stays a plain failure.
    /// </summary>
    private static FileInstallBlockedException? BlockedIfNothingMoved(string sourcePath, string outputPath, string failedPath, bool isOriginal, Exception exception) =>
        exception is IOException or UnauthorizedAccessException && File.Exists(sourcePath) && File.Exists(outputPath)
            ? new FileInstallBlockedException(failedPath, isOriginal, IsInUse(exception), exception)
            : null;

    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION: another process has the file open.
    private static bool IsInUse(Exception exception) => exception is IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) };

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
        }
        catch (Exception exception) when (BlockedIfNothingMoved(sourcePath, outputPath, sourcePath, isOriginal: true, exception) is { } blocked)
        {
            throw blocked;
        }

        try
        {
            File.Move(outputPath, installedPath);
            EnsureInstalled(installedPath);
        }
        catch (Exception exception)
        {
            if (!File.Exists(sourcePath) && File.Exists(displacedPath)) File.Move(displacedPath, sourcePath);
            if (BlockedIfNothingMoved(sourcePath, outputPath, outputPath, isOriginal: false, exception) is { } blocked) throw blocked;
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
