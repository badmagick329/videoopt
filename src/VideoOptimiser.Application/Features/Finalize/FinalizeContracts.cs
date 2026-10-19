using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Finalize;

public sealed record FinalizationResult(JobRecord? Job, ExitCode ExitCode, string Message);

public interface IFinalizationService
{
    Task<FinalizationResult> FinalizeAsync(string databasePath, Guid jobId, AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where finalisation put the AV1, and where <c>archive</c> moved the original. <paramref name="Warning"/> is set when the AV1
/// is installed but something failed afterwards, such as copying the original's file dates. <paramref name="LeftoverOriginalPath"/>
/// is where <c>delete</c> left the original when deleting it failed: the AV1 is installed all the same.
/// </summary>
public sealed record FileInstallation(string InstalledPath, string? ArchivedOriginalPath, string? Warning = null, string? LeftoverOriginalPath = null);

/// <summary>
/// The installer could not move a file and found both files where they started, with the original back at its path, so the
/// job can simply be finalised again (VOPT-143). <paramref name="InUse"/> marks a sharing violation: another program, such as
/// a player the user reviewed the AV1 in, has the file open.
/// </summary>
public sealed class FileInstallBlockedException(string path, bool isOriginal, bool inUse, Exception inner) : Exception(inner.Message, inner)
{
    public string Path { get; } = path;
    public bool IsOriginal { get; } = isOriginal;
    public bool InUse { get; } = inUse;
}

/// <summary>
/// Where an install will put each file, worked out before anything moves so the caller can record <paramref name="DisplacedPath"/>
/// first (VOPT-144): a crash between the installer's two moves leaves the original there, and nothing else names it.
/// <paramref name="Action"/> is the original action; <paramref name="DisplacedPath"/> is the rollback file for <c>delete</c>,
/// the archive path for <c>archive</c>, and null for <c>keep</c>, which never moves the original.
/// </summary>
public sealed record FileInstallPlan(string SourcePath, string OutputPath, string Action, string InstalledPath, string? DisplacedPath);

public interface ISafeFileInstaller
{
    /// <summary>Only reads the file system; nothing is created or moved.</summary>
    FileInstallPlan Plan(string sourcePath, string outputPath, OriginalSettings original);

    /// <summary>Its first line is its only cancellation point: once a file has moved, it runs to the end.</summary>
    Task<FileInstallation> InstallAsync(FileInstallPlan plan, CancellationToken cancellationToken = default);
}
