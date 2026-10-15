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
/// is installed but something minor failed afterwards, such as copying the original's file dates.
/// </summary>
public sealed record FileInstallation(string InstalledPath, string? ArchivedOriginalPath, string? Warning = null);

public interface ISafeFileInstaller
{
    Task<FileInstallation> InstallAsync(string sourcePath, string outputPath, OriginalSettings original, CancellationToken cancellationToken = default);
}
