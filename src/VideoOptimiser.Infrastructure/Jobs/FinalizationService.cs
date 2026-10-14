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
    public const string InterruptedMessage = "Finalisation was interrupted. Run 'video-optimiser immich finalize' again to resume.";

    /// <summary>
    /// An Immich job that a crash left mid-finalisation: still <see cref="JobStatus.Finalizing"/>, or marked interrupted on
    /// its way out of it. Unlike a local file, nothing is half-moved, so it is ready again rather than needing review.
    /// </summary>
    public static bool IsInterruptedImmichFinalization(JobRecord job) => job.SourceKind == JobSourceKind.Immich && IsInterruptedFinalization(job);

    /// <summary>
    /// A file job that Ctrl+C or a crash left mid-finalisation. <see cref="FinalizeAsync"/> resumes it only after checking that
    /// nothing moved; otherwise it needs review (VOPT-70).
    /// </summary>
    public static bool IsInterruptedFileFinalization(JobRecord job) => job.SourceKind == JobSourceKind.File && IsInterruptedFinalization(job);

    private static bool IsInterruptedFinalization(JobRecord job) =>
        job.Status == JobStatus.Finalizing || job.Status == JobStatus.Interrupted && job.ResumeStatus == JobStatus.Finalizing;

    public const string ManualReviewCategory = "ManualInterventionRequired";
    public const string ManualReviewMessage = "Finalisation was interrupted and requires manual review.";

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
        var resumingFile = IsInterruptedFileFinalization(job);
        if (job.Status != JobStatus.ReadyToFinalize && !resumingFile || job.OutputPath is null) return new FinalizationResult(job, ExitCode.ValidationFailure, "Job is not ready to finalise.");
        var fromImmich = job.SourceKind == JobSourceKind.Immich;
        // VOPT-60: a missing file stops only this job, which stays as it is, so a batch finalises the rest and prints its
        // summary. An interrupted file finalisation skips these: a missing file there means something moved, which it reviews.
        if (!resumingFile && MissingFile(job, fromImmich) is { } missing) return new FinalizationResult(job, ExitCode.FinalisationFailure, missing);
        if (await TryLoadManifestAsync(job.OutputPath, cancellationToken) is not { } manifest)
        {
            // Without the record an interrupted file finalisation cannot show that nothing moved, and converting again (F2's
            // advice) would queue a path that may now hold the AV1 or nothing at all.
            if (resumingFile) return await RequireReviewAsync(databasePath, job);
            return new FinalizationResult(job, ExitCode.FinalisationFailure, $"The AV1's validation record is missing or unreadable: {manifests.GetPath(job.OutputPath)}, so the AV1 can't be checked. To convert the video again, run 'video-optimiser queue cancel {job.Id:N}', then 'video-optimiser queue retry {job.Id:N}'.");
        }
        if (manifest.Validation?.Passed != true)
        {
            if (resumingFile) return await RequireReviewAsync(databasePath, job);
            return new FinalizationResult(job, ExitCode.ValidationFailure, "Job validation did not pass.");
        }
        // Everything up to the Finalizing write only reads, so Ctrl+C there leaves the job as it was. Once written, the state
        // writes use CancellationToken.None: a cancelled write would leave the stored state behind what the files did.
        var finalizing = false;
        try
        {
            if (resumingFile)
            {
                // The installer moves the original aside before the AV1 takes its place and puts it back on failure, and keep
                // never moves the original, so both files where they started, with the original unchanged, means nothing moved.
                if (!File.Exists(manifest.SourcePath) || !File.Exists(manifest.OutputPath) || await fingerprints.CreateAsync(manifest.SourcePath, cancellationToken) != manifest.SourceFingerprint)
                {
                    return await RequireReviewAsync(databasePath, job);
                }
                ClearToReady(job);
                await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
            }
            // F3: an Immich job's staged copy is only a download. Without it there is nothing to compare here, and Immich's
            // checksum check in ImmichFinalizer still guards the asset that is replaced.
            else if (!(fromImmich && !File.Exists(manifest.SourcePath)) && await fingerprints.CreateAsync(manifest.SourcePath, cancellationToken) != manifest.SourceFingerprint)
            {
                throw new FinalizationRefusedException("Original changed after encoding.");
            }

            job.Status = JobStatus.Finalizing;
            await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
            finalizing = true;
            var message = fromImmich
                ? await immich.ReplaceAsync(databasePath, job, manifest, settings, cancellationToken)
                : Describe(manifest.SourcePath, settings.Original, await installer.InstallAsync(manifest.SourcePath, manifest.OutputPath, settings.Original, cancellationToken));
            job.Status = JobStatus.Completed;
            job.FailureCategory = null;
            job.FailureMessage = null;
            job.CompletedUtc = DateTimeOffset.UtcNow;
            await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
            if (fromImmich) message += immich.DeleteStaging(job, manifest, settings);
            return new FinalizationResult(job, ExitCode.Success, message);
        }
        catch (OperationCanceledException) when (fromImmich)
        {
            if (finalizing) await ReturnToReadyAsync(databasePath, job, InterruptedMessage);
            throw;
        }
        catch (OperationCanceledException)
        {
            // A file job's last cancellation point is the installer's first line; after it the installer runs to the end
            // without one. So a cancelled file finalisation moved nothing, and the job is simply ready again.
            if (finalizing)
            {
                ClearToReady(job);
                await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
            }
            throw;
        }
        catch (FinalizationWaitsException exception)
        {
            // The user has to act in Immich first, and the message says how; a resume suffix would contradict it.
            await ReturnToReadyAsync(databasePath, job, exception.Message);
            return new FinalizationResult(job, ExitCode.FinalisationFailure, exception.Message);
        }
        catch (Exception exception) when (fromImmich && exception is not FinalizationRefusedException)
        {
            // Every Immich step before the trash can run again, and the AV1's asset ID is already saved once uploaded, so
            // the job goes back to ready rather than failing: the next finalize continues where this one stopped.
            await ReturnToReadyAsync(databasePath, job, exception.Message);
            return new FinalizationResult(job, ExitCode.FinalisationFailure, $"{exception.Message} Run 'video-optimiser immich finalize' again to resume.");
        }
        catch (Exception exception)
        {
            job.Status = JobStatus.Failed;
            job.FailureCategory = "FinalizationFailed";
            job.FailureMessage = exception.Message;
            job.CompletedUtc = DateTimeOffset.UtcNow;
            await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
            return new FinalizationResult(job, ExitCode.FinalisationFailure, exception.Message);
        }
    }

    /// <summary>Plain ready, with no failure category: unlike <see cref="ReturnToReady"/>, which marks an Immich finalisation that stopped partway.</summary>
    private static void ClearToReady(JobRecord job)
    {
        job.Status = JobStatus.ReadyToFinalize;
        job.ResumeStatus = null;
        job.FailureCategory = null;
        job.FailureMessage = null;
    }

    /// <summary>
    /// A file finalisation interrupted after something moved. <see cref="JobRecord.ResumeStatus"/> stays <see cref="JobStatus.Finalizing"/>,
    /// which keeps <c>cleanup</c> off the AV1: it may be the only copy left.
    /// </summary>
    private async Task<FinalizationResult> RequireReviewAsync(string databasePath, JobRecord job)
    {
        job.Status = JobStatus.Failed;
        job.ResumeStatus = JobStatus.Finalizing;
        job.FailureCategory = ManualReviewCategory;
        job.FailureMessage = ManualReviewMessage;
        job.CompletedUtc = DateTimeOffset.UtcNow;
        await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
        return new FinalizationResult(job, ExitCode.FinalisationFailure, ManualReviewMessage);
    }

    /// <summary>
    /// F4 for a folder job's original and F1 for the AV1, or null. An Immich job's missing staged copy is not one (F3), and
    /// neither is its AV1 once uploaded: finalisation then uses the asset and never reads the local file.
    /// </summary>
    private static string? MissingFile(JobRecord job, bool fromImmich)
    {
        if (!fromImmich && !File.Exists(job.SourcePath))
        {
            // If the user deleted the original, the AV1 is the only copy, and cancelling the job lets cleanup delete it.
            return $"{OriginalMissing(job)} If you moved it, put it back and run 'video-optimiser finalize {job.Id:N}' again. If you deleted it on purpose, the AV1 at {job.OutputPath} is now the only copy: move it somewhere safe before running 'video-optimiser queue cancel {job.Id:N}'.";
        }
        if (!File.Exists(job.OutputPath) && !(fromImmich && job.ImmichNewAssetId is not null))
        {
            return $"{Av1Missing(job)} If you moved it, put it back and run 'video-optimiser finalize {job.Id:N}' again. To convert the video again instead, run 'video-optimiser queue cancel {job.Id:N}', then 'video-optimiser queue retry {job.Id:N}'.";
        }
        return null;
    }

    /// <summary>The first sentence of F4; <c>validate</c> prints it alone.</summary>
    public static string OriginalMissing(JobRecord job) => $"Original does not exist: {job.SourcePath}.";

    /// <summary>The first sentence of F1; <c>validate</c> prints it alone.</summary>
    public static string Av1Missing(JobRecord job) => $"The AV1 is missing: {job.OutputPath}.";

    private async Task<OutputManifest?> TryLoadManifestAsync(string outputPath, CancellationToken cancellationToken)
    {
        try
        {
            return await manifests.LoadAsync(outputPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
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

/// <summary>
/// An Immich finalisation that waits on the user: the job returns to ready, and the message, which says what to do in Immich
/// and which command to run next, is shown as it is.
/// </summary>
internal sealed class FinalizationWaitsException(string message) : Exception(message);
