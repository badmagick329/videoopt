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
        if (job.Status != JobStatus.ReadyToFinalize || job.OutputPath is null) return new FinalizationResult(job, ExitCode.ValidationFailure, "Job is not ready to finalize.");
        if (!settings.Original.Action.Equals("delete", StringComparison.OrdinalIgnoreCase)) return new FinalizationResult(job, ExitCode.InvalidConfiguration, "finalize currently requires original.action: delete.");
        var manifest = await manifests.LoadAsync(job.OutputPath, cancellationToken);
        if (manifest.Validation?.Passed != true) return new FinalizationResult(job, ExitCode.ValidationFailure, "Job validation did not pass.");
        job.Status = JobStatus.Finalizing;
        await jobs.UpdateAsync(databasePath, job, cancellationToken);
        try
        {
            if (await fingerprints.CreateAsync(manifest.SourcePath, cancellationToken) != manifest.SourceFingerprint) throw new InvalidOperationException("Source changed after encoding.");
            await installer.InstallAsync(manifest.SourcePath, manifest.OutputPath, cancellationToken);
            job.Status = JobStatus.Completed;
            job.CompletedUtc = DateTimeOffset.UtcNow;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            return new FinalizationResult(job, ExitCode.Success, "Finalization complete. Original deleted.");
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
}
