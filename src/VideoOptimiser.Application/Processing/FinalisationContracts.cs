using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Processing;

/// <summary>
/// Where finalisation put the AV1, and where <c>archive</c> moved the original. <paramref name="Warning"/> is set when the AV1
/// is installed but something minor failed afterwards, such as copying the original's file dates.
/// </summary>
public sealed record FileInstallation(string InstalledPath, string? ArchivedOriginalPath, string? Warning = null);

public interface ISafeFileInstaller
{
    Task<FileInstallation> InstallAsync(string sourcePath, string outputPath, OriginalSettings original, CancellationToken cancellationToken = default);
}
