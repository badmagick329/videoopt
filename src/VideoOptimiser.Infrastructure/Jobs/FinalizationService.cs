using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Immich;

namespace VideoOptimiser.Infrastructure.Jobs;

public sealed class FinalizationService(IJobRepository jobs, IOutputManifestStore manifests, IFileFingerprintService fingerprints, ISafeFileInstaller installer, ImmichFinalizer immich) : IFinalizationService
{
    /// <summary>A ready Immich job whose last finalisation stopped early carries this category until it completes.</summary>
    public const string IncompleteCategory = "FinalizationIncomplete";
    public const string InterruptedMessage = "Finalisation was interrupted. Run finalize again to resume.";

    /// <summary>
    /// An Immich job that a crash left mid-finalisation: still <see cref="JobStatus.Finalizing"/>, or marked interrupted on
    /// its way out of it. Unlike a local file, nothing is half-moved, so it is ready again rather than needing review.
    /// </summary>
    public static bool IsInterruptedImmichFinalization(JobRecord job) =>
        job.SourceKind == JobSourceKind.Immich &&
        (job.Status == JobStatus.Finalizing || job.Status == JobStatus.Interrupted && job.ResumeStatus == JobStatus.Finalizing);

    public static void ReturnToReady(JobRecord job, string message)
    {
        job.Status = JobStatus.ReadyToFinalize;
        job.ResumeStatus = null;
        job.FailureCategory = IncompleteCategory;
        job.FailureMessage = message;
    }

    public async Task<FinalizationResult> FinalizeAsync(string databasePath, Guid jobId, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(databasePath, jobId, cancellationToken);
        if (job is null) return new FinalizationResult(null, ExitCode.ProcessingFailure, "Job was not found.");
        if (IsInterruptedImmichFinalization(job))
        {
            ReturnToReady(job, InterruptedMessage);
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
        }
        if (job.Status != JobStatus.ReadyToFinalize || job.OutputPath is null) return new FinalizationResult(job, ExitCode.ValidationFailure, "Job is not ready to finalise.");
        var manifest = await manifests.LoadAsync(job.OutputPath, cancellationToken);
        if (manifest.Validation?.Passed != true) return new FinalizationResult(job, ExitCode.ValidationFailure, "Job validation did not pass.");
        var fromImmich = job.SourceKind == JobSourceKind.Immich;
        job.Status = JobStatus.Finalizing;
        await jobs.UpdateAsync(databasePath, job, cancellationToken);
        try
        {
            if (await fingerprints.CreateAsync(manifest.SourcePath, cancellationToken) != manifest.SourceFingerprint) throw new FinalizationRefusedException("Original changed after encoding.");
            var message = fromImmich
                ? await immich.ReplaceAsync(databasePath, job, manifest, settings, cancellationToken)
                : Describe(manifest.SourcePath, settings.Original, await installer.InstallAsync(manifest.SourcePath, manifest.OutputPath, settings.Original, cancellationToken));
            job.Status = JobStatus.Completed;
            job.FailureCategory = null;
            job.FailureMessage = null;
            job.CompletedUtc = DateTimeOffset.UtcNow;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            if (fromImmich) message += immich.DeleteStaging(job, manifest, settings);
            return new FinalizationResult(job, ExitCode.Success, message);
        }
        catch (OperationCanceledException) when (fromImmich)
        {
            await ReturnToReadyAsync(databasePath, job, InterruptedMessage);
            throw;
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
        catch (Exception exception) when (fromImmich && exception is not FinalizationRefusedException)
        {
            // Every Immich step before the trash can run again, and the AV1's asset ID is already saved once uploaded, so
            // the job goes back to ready rather than failing: the next finalize continues where this one stopped.
            await ReturnToReadyAsync(databasePath, job, exception.Message);
            return new FinalizationResult(job, ExitCode.FinalisationFailure, $"{exception.Message} Run finalize again to resume.");
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

    private async Task ReturnToReadyAsync(string databasePath, JobRecord job, string message)
    {
        ReturnToReady(job, message);
        await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
    }

    /// <summary>
    /// The mode comes from the action, not the paths: the AV1 takes the output's extension, so a changed path no longer
    /// means the original was kept.
    /// </summary>
    internal static string Describe(string sourcePath, OriginalSettings original, FileInstallation installation)
    {
        var moved = string.Equals(installation.InstalledPath, sourcePath, StringComparison.Ordinal) ? string.Empty : $"; the AV1 is {installation.InstalledPath}";
        var message = original.Action.Equals(OriginalActions.Keep, StringComparison.OrdinalIgnoreCase) ? $"Original kept; AV1 installed as {installation.InstalledPath}."
            : installation.ArchivedOriginalPath is { } archived ? $"Original archived to {archived}{moved}."
            : $"Original deleted{moved}.";
        return installation.Warning is { } warning ? $"{message} Warning: {warning}" : message;
    }
}

/// <summary>A finalisation that running again cannot fix: the original no longer matches what was converted.</summary>
internal sealed class FinalizationRefusedException(string message) : Exception(message);
