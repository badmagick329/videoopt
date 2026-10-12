using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Infrastructure.Jobs;

public sealed class FinalizationService(IJobRepository jobs, IOutputManifestStore manifests, IFileFingerprintService fingerprints, ISafeFileInstaller installer) : IFinalizationService
{
    public async Task<FinalizationResult> FinalizeAsync(string databasePath, Guid jobId, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(databasePath, jobId, cancellationToken);
        if (job is null) return new FinalizationResult(null, ExitCode.ProcessingFailure, "Job was not found.");
        if (job.Status != JobStatus.ReadyToFinalize || job.OutputPath is null) return new FinalizationResult(job, ExitCode.ValidationFailure, "Job is not ready to finalise.");
        var manifest = await manifests.LoadAsync(job.OutputPath, cancellationToken);
        if (manifest.Validation?.Passed != true) return new FinalizationResult(job, ExitCode.ValidationFailure, "Job validation did not pass.");
        job.Status = JobStatus.Finalizing;
        await jobs.UpdateAsync(databasePath, job, cancellationToken);
        try
        {
            if (await fingerprints.CreateAsync(manifest.SourcePath, cancellationToken) != manifest.SourceFingerprint) throw new InvalidOperationException("Original changed after encoding.");
            var installation = await installer.InstallAsync(manifest.SourcePath, manifest.OutputPath, settings.Original, cancellationToken);
            job.Status = JobStatus.Completed;
            job.CompletedUtc = DateTimeOffset.UtcNow;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            return new FinalizationResult(job, ExitCode.Success, Describe(manifest.SourcePath, installation));
        }
        catch (OperationCanceledException)
        {
            job.ResumeStatus = JobStatus.Finalizing;
            job.Status = JobStatus.Interrupted;
            job.FailureCategory = "ManualInterventionRequired";
            job.FailureMessage = "Finalisation was interrupted and requires manual review.";
            await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            job.Status = JobStatus.Failed;
            job.FailureCategory = "FinalizationFailed";
            job.FailureMessage = exception.Message;
            job.CompletedUtc = DateTimeOffset.UtcNow;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            return new FinalizationResult(job, ExitCode.FinalisationFailure, exception.Message);
        }
    }

    private static string Describe(string sourcePath, FileInstallation installation) =>
        installation.ArchivedOriginalPath is { } archived ? $"Finalisation complete. Original archived to {archived}."
        : installation.InstalledPath != sourcePath ? $"Finalisation complete. Original kept; AV1 installed as {installation.InstalledPath}."
        : "Finalisation complete. Original deleted.";
}
