using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Finalize;

public sealed class FinalizationService(IJobRepository jobs, IOutputManifestStore manifests, IFileFingerprintService fingerprints, ISafeFileInstaller installer, ImmichFinalizer immich, ICommandHints hints) : IFinalizationService
{
    public async Task<FinalizationResult> FinalizeAsync(string databasePath, Guid jobId, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(databasePath, jobId, cancellationToken);
        if (job is null) return new FinalizationResult(null, ExitCode.ProcessingFailure, "Job was not found.");
        if (FinalizationRules.IsInterruptedImmich(job))
        {
            FinalizationRules.ReturnToReady(job, FinalizationRules.InterruptedMessage(hints.Command("immich finalize")));
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
        }
        // A failed review is checked again like an interrupted file: VOPT-85, jobs an older version failed with nothing moved.
        var resumingFile = FinalizationRules.IsInterruptedFile(job) || FinalizationRules.IsFailedFileReview(job);
        if (job.Status != JobStatus.ReadyToFinalize && !resumingFile || job.OutputPath is null) return new FinalizationResult(job, ExitCode.ValidationFailure, "Job is not ready to finalise.");
        var fromImmich = job.SourceKind == JobSourceKind.Immich;
        // VOPT-60: a missing file stops only this job, which stays as it is, so a batch finalises the rest and prints its
        // summary. An interrupted file finalisation skips these: a missing file there means something moved, which it reviews.
        if (!resumingFile && MissingFile(job, fromImmich) is { } missing) return new FinalizationResult(job, ExitCode.FinalisationFailure, missing);
        if (await manifests.TryLoadAsync(job.OutputPath, cancellationToken) is not { } manifest)
        {
            // Without the record an interrupted file finalisation cannot show that nothing moved, and converting again (F2's
            // advice) would queue a path that may now hold the AV1 or nothing at all.
            if (resumingFile) return await RequireReviewAsync(databasePath, job, FinalizationRules.ManualReviewMessage);
            return new FinalizationResult(job, ExitCode.FinalisationFailure, $"The AV1's validation record is missing or unreadable: {manifests.GetPath(job.OutputPath)}, so the AV1 can't be checked. To convert the video again, run '{hints.Command($"queue drop {job.Id:N}")}', then '{hints.Command($"queue retry {job.Id:N}")}'.");
        }
        if (manifest.Validation?.Passed != true)
        {
            if (resumingFile) return await RequireReviewAsync(databasePath, job, ReviewMessage(manifest));
            return new FinalizationResult(job, ExitCode.ValidationFailure, "Job validation did not pass.");
        }
        // Everything up to the Finalizing write only reads, so Ctrl+C there leaves the job as it was. Once written, the state
        // writes use CancellationToken.None: a cancelled write would leave the stored state behind what the files did.
        var finalizing = false;
        try
        {
            if (resumingFile)
            {
                // VOPT-144: cut off between the installer's two moves, the original is where the record says it was going and
                // the AV1 has not moved. Putting the original back makes it a finalisation that moved nothing, checked below.
                if (await CutOffBetweenTheMovesAsync(manifest, cancellationToken))
                {
                    try
                    {
                        File.Move(manifest.DisplacedOriginalPath!, manifest.SourcePath);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        return await RequireReviewAsync(databasePath, job, ReviewMessage(manifest));
                    }
                }
                // The installer moves the original aside before the AV1 takes its place and puts it back on failure, and keep
                // never moves the original, so both files where they started, with the original unchanged, means nothing moved.
                if (!File.Exists(manifest.SourcePath) || !File.Exists(manifest.OutputPath) || await fingerprints.CreateAsync(manifest.SourcePath, cancellationToken) != manifest.SourceFingerprint)
                {
                    return await RequireReviewAsync(databasePath, job, ReviewMessage(manifest));
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
            string message;
            if (fromImmich)
            {
                message = await immich.ReplaceAsync(databasePath, job, manifest, settings, cancellationToken);
            }
            else
            {
                // Recorded before anything moves, so a crash between the installer's moves leaves the original findable
                // (VOPT-144). Nothing here takes the token, so the installer's first line stays the last cancellation point.
                var plan = installer.Plan(manifest.SourcePath, manifest.OutputPath, settings.Original);
                manifest.DisplacedOriginalPath = plan.DisplacedPath;
                await manifests.SaveAsync(manifest, CancellationToken.None);
                message = Describe(manifest.SourcePath, settings.Original, await installer.InstallAsync(plan, cancellationToken));
            }
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
            if (finalizing) await ReturnToReadyAsync(databasePath, job, FinalizationRules.InterruptedMessage(hints.Command("immich finalize")));
            throw;
        }
        catch (OperationCanceledException)
        {
            // A file job's last cancellation point is the installer's first line: the plan and the record's save before it take
            // no token, and after it the installer runs to the end without one. So a cancelled file finalisation moved
            // nothing, and the job is simply ready again.
            if (finalizing)
            {
                ClearToReady(job);
                await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
                await ForgetDisplacedOriginalAsync(manifest);
            }
            throw;
        }
        catch (FinalizationWaitsException exception)
        {
            // The user has to act in Immich first, and the message says how; a resume suffix would contradict it.
            await ReturnToReadyAsync(databasePath, job, exception.Message);
            return new FinalizationResult(job, ExitCode.FinalisationFailure, exception.Message);
        }
        catch (FileInstallBlockedException exception)
        {
            // Nothing moved, so the job is ready again: failing it would leave only `queue retry`, a second encode (VOPT-143).
            var message = Blocked(job, exception);
            await ReturnToReadyAsync(databasePath, job, message);
            await ForgetDisplacedOriginalAsync(manifest);
            return new FinalizationResult(job, ExitCode.FinalisationFailure, message);
        }
        catch (Exception exception) when (fromImmich && exception is not FinalizationRefusedException)
        {
            // Every Immich step before the trash can run again, and the AV1's asset ID is already saved once uploaded, so
            // the job goes back to ready rather than failing: the next finalize continues where this one stopped.
            await ReturnToReadyAsync(databasePath, job, exception.Message);
            return new FinalizationResult(job, ExitCode.FinalisationFailure, $"{exception.Message} Run '{hints.Command("immich finalize")}' again to resume.");
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

    /// <summary>Plain ready, with no failure category: unlike <see cref="FinalizationRules.ReturnToReady"/>, which marks an Immich finalisation that stopped partway.</summary>
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
    private async Task<FinalizationResult> RequireReviewAsync(string databasePath, JobRecord job, string message)
    {
        if (FinalizationRules.IsFailedFileReview(job))
        {
            // Already failed for review: the check found it still so, and rewriting would only move its failure time. The
            // message alone may change, as when the record now shows where the original is.
            if (job.FailureMessage != message)
            {
                job.FailureMessage = message;
                await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
            }
            return new FinalizationResult(job, ExitCode.FinalisationFailure, message);
        }
        job.Status = JobStatus.Failed;
        job.ResumeStatus = JobStatus.Finalizing;
        job.FailureCategory = FailureCategories.ManualInterventionRequired;
        job.FailureMessage = message;
        job.CompletedUtc = DateTimeOffset.UtcNow;
        await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
        return new FinalizationResult(job, ExitCode.FinalisationFailure, message);
    }

    /// <summary>Names the original's recorded new path while a file is there: after something moved, it may be the only place the original is.</summary>
    private static string ReviewMessage(OutputManifest manifest) =>
        manifest.DisplacedOriginalPath is { } displaced && File.Exists(displaced) ? FinalizationRules.ManualReviewMessageWithOriginal(displaced) : FinalizationRules.ManualReviewMessage;

    /// <summary>
    /// VOPT-144: a crash between the installer's two moves. The original is gone from its path and found where the record
    /// says it was going, unchanged (fingerprinted as if still at its path, which a fingerprint covers); the AV1 has not moved.
    /// </summary>
    private async Task<bool> CutOffBetweenTheMovesAsync(OutputManifest manifest, CancellationToken cancellationToken) =>
        !File.Exists(manifest.SourcePath) && manifest.DisplacedOriginalPath is { } displaced && File.Exists(displaced) && File.Exists(manifest.OutputPath)
        && await fingerprints.CreateAsync(displaced, manifest.SourcePath, cancellationToken) == manifest.SourceFingerprint;

    /// <summary>
    /// Nothing moved, so the recorded path names no original. Left behind, it could name a different file that later takes
    /// that archive name if a later attempt is cut off before its own plan is saved. Best effort: failing to clear it must
    /// not replace the outcome being reported.
    /// </summary>
    private async Task ForgetDisplacedOriginalAsync(OutputManifest manifest)
    {
        if (manifest.DisplacedOriginalPath is null) return;
        manifest.DisplacedOriginalPath = null;
        try
        {
            await manifests.SaveAsync(manifest, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// F4 for a folder job's original and F1 for the AV1, or null. An Immich job's missing staged copy is not one (F3), and
    /// neither is its AV1 once uploaded: finalisation then uses the asset and never reads the local file.
    /// </summary>
    private string? MissingFile(JobRecord job, bool fromImmich)
    {
        if (!fromImmich && !File.Exists(job.SourcePath))
        {
            // If the user deleted the original, the AV1 is the only copy, and cancelling the job lets cleanup delete it.
            return $"{FinalizationRules.OriginalMissing(job)} If you moved it, put it back and run '{hints.Command($"finalize {job.Id:N}")}' again. If you deleted it on purpose, the AV1 at {job.OutputPath} is now the only copy: move it somewhere safe before running '{hints.Command($"queue drop {job.Id:N}")}'.";
        }
        if (!File.Exists(job.OutputPath) && !(fromImmich && job.ImmichNewAssetId is not null))
        {
            return $"{FinalizationRules.Av1Missing(job)} If you moved it, put it back and run '{hints.Command($"finalize {job.Id:N}")}' again. To convert the video again instead, run '{hints.Command($"queue drop {job.Id:N}")}', then '{hints.Command($"queue retry {job.Id:N}")}'.";
        }
        return null;
    }

    private string Blocked(JobRecord job, FileInstallBlockedException blocked)
    {
        var file = blocked.IsOriginal ? "original" : "AV1";
        var again = hints.Command($"finalize {job.Id:N}");
        return blocked.InUse
            ? $"The {file} is open in another program: {blocked.Path}. Nothing was moved. Close it, then run '{again}' again."
            : $"The {file} could not be moved: {blocked.Message} Nothing was moved. Fix that, then run '{again}' again.";
    }

    private async Task ReturnToReadyAsync(string databasePath, JobRecord job, string message)
    {
        FinalizationRules.ReturnToReady(job, message);
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
            // The warning says where the original is and why it is still there.
            : installation.LeftoverOriginalPath is not null ? $"AV1 installed as {installation.InstalledPath}."
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
