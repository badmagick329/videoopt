using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Finalize;

/// <summary>
/// Replaces an Immich original with its AV1: uploads the output as a new asset, copies what Immich keeps beside the file
/// (description, rating, albums, favourite, shared links, stack, sidecar, tags), and only then moves the original to
/// Immich's trash. Swapping the file inside the original asset is not an option: Immich's integrity check flags a file
/// that no longer matches its checksum, and a changed checksum makes the phone upload the original again.
/// Every step before the trash can run again, so any failure leaves the original live and the job resumable. The trash waits
/// until the new asset's checksum matches the AV1 that passed validation (VOPT-145).
/// </summary>
public sealed class ImmichFinalizer(IImmichClientFactory clients, IJobRepository jobs, IOutputManifestStore manifests, ICommandHints hints)
{
    public async Task<string> ReplaceAsync(string databasePath, JobRecord job, OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken)
    {
        if (job.ImmichAssetId is not { } originalId || job.ImmichChecksum is not { } checksum)
        {
            throw new FinalizationRefusedException("The job has no Immich asset ID or checksum.");
        }

        var client = clients.Create(settings.Immich);
        // Before the upload, so a trash that is off leaves no AV1 beside the original; the job returns to ready.
        await ImmichTrash.RequireOnAsync(client, cancellationToken);
        ImmichAsset original;
        try
        {
            original = await client.GetAssetAsync(originalId, cancellationToken);
        }
        catch (ImmichApiException exception) when (exception.StatusCode is 400 or 404)
        {
            // F5. Immich answers 400 "Not found or no asset.read access" for a deleted asset and for one in the Locked Folder,
            // so the message offers both. A trashed asset stays readable and never gets here.
            throw new FinalizationWaitsException(await OriginalNotFoundAsync(client, job, cancellationToken));
        }
        if (!string.Equals(original.Checksum, checksum, StringComparison.Ordinal))
        {
            throw new FinalizationRefusedException($"The original of {OriginalName(job)} changed in Immich, so it no longer holds the file that was converted.");
        }

        string replacement;
        if (original.IsTrashed)
        {
            // Most likely an earlier run trashed it and stopped before marking the job completed, but the user may have
            // trashed it by hand before the copy finished, so the copy steps run again below.
            if (job.ImmichNewAssetId is not { } replaced) throw new FinalizationWaitsException($"The original of {OriginalName(job)} is in Immich's trash. Restore it, then run '{hints.Command("immich finalize")}' again.");
            replacement = await RequireLiveAsync(client, job, replaced, originalId, manifest, cancellationToken);
        }
        else
        {
            replacement = await ReplacementAsync(client, databasePath, job, original, manifest, cancellationToken);
        }

        // Description and rating exist only in Immich; setting them through the API locks them, so the metadata extraction
        // Immich runs on the new file later leaves them alone. Capture date, place and camera travel inside the AV1 file.
        var description = string.IsNullOrEmpty(original.Exif.Description) ? null : original.Exif.Description;
        if (description is not null || original.Exif.Rating is not null)
        {
            await client.UpdateMetadataAsync(replacement, new ImmichMetadataUpdate(Description: description, Rating: original.Exif.Rating), cancellationToken);
        }

        await client.CopyRelationsAsync(originalId, replacement, cancellationToken);
        await client.TagAssetAsync(replacement, original.TagIds, cancellationToken);
        if (!original.IsTrashed) await client.TrashAsync([originalId], cancellationToken);
        return $"The AV1 is in Immich as {Av1Name(job)}; the original is in Immich's trash.";
    }

    /// <summary>
    /// Deletes what the job kept locally: the asset's staging folder, which holds the staged original and every attempt's
    /// temporary output and manifest. Runs after the job is completed, so a failure only leaves files behind; the returned
    /// text says so, or is empty.
    /// </summary>
    public string DeleteStaging(JobRecord job, OutputManifest manifest, AppSettings settings)
    {
        try
        {
            if (ImmichStaging.IsStagedOriginal(settings.Immich.StagingDirectory, job.ImmichAssetId, job.SourcePath))
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(job.SourcePath))!;
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                return string.Empty;
            }

            // A source outside its asset's staging folder is not this job's to delete; only the output and manifest are.
            File.Delete(manifest.OutputPath);
            File.Delete(manifests.GetPath(manifest.OutputPath));
            return string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $" Could not delete the staged files: {exception.Message}";
        }
    }

    /// <summary>
    /// The AV1's asset ID, uploading it unless an earlier run already did. The ID is saved before anything else happens to
    /// the asset: discovery leaves every job's AV1 asset alone, and without the ID the next run would treat this old-dated
    /// video as a candidate and convert it again.
    /// </summary>
    private async Task<string> ReplacementAsync(IImmichClient client, string databasePath, JobRecord job, ImmichAsset original, OutputManifest manifest, CancellationToken cancellationToken)
    {
        if (job.ImmichNewAssetId is { } stored)
        {
            try
            {
                return await RequireLiveAsync(client, job, stored, original.Id, manifest, cancellationToken);
            }
            catch (ImmichApiException exception) when (exception.Code == "ImmichRequestFailed")
            {
                // Unreadable, most likely purged. Uploading again is safe: if it still exists, Immich answers with its ID.
            }
        }

        var outputPath = manifest.OutputPath;
        // An AV1 that changed after validation would only be refused once uploaded, leaving a second video beside the
        // original for the user to delete; checked first, nothing reaches Immich.
        if (manifest.Validation?.Av1Checksum is { } validated && await ImmichStaging.Sha1Base64Async(outputPath, cancellationToken) != validated)
        {
            throw new FinalizationWaitsException($"The AV1 at {outputPath} changed after it passed validation, so it wasn't uploaded and the original stays in Immich. Run '{hints.Command($"validate {job.Id:N}")}' to check it again, then '{hints.Command("immich finalize")}'.");
        }
        var fileName = Path.ChangeExtension(original.OriginalFileName, Path.GetExtension(outputPath));
        var upload = await client.UploadAsync(new ImmichUpload(outputPath, fileName, original.FileCreatedAt, original.FileModifiedAt, original.IsFavorite), cancellationToken: cancellationToken);
        job.ImmichNewAssetId = upload.AssetId;
        await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
        // Read back even when new: Immich computes the checksum from the bytes it received, so this is what proves the upload
        // arrived whole. A duplicate answer names whichever asset already holds those bytes, which may be in the trash.
        return await RequireLiveAsync(client, job, upload.AssetId, original.Id, manifest, cancellationToken);
    }

    /// <summary>The file names the user finds the original and its AV1 under in Immich: the asset IDs are no use to someone looking at the web app.</summary>
    private static string OriginalName(JobRecord job) => Path.GetFileName(job.SourcePath);

    private static string Av1Name(JobRecord job) => Path.ChangeExtension(OriginalName(job), Path.GetExtension(job.OutputPath));

    /// <summary>
    /// The AV1's asset ID once it is shown to be the AV1 that passed validation (VOPT-145): live, not the original, and with
    /// the validated checksum. Every path to the trash call and to the job's completion passes here, a fresh upload and a
    /// resume alike, because once Immich empties its trash the original is gone and this asset is all that is left.
    /// </summary>
    /// <remarks>
    /// A mismatched asset is left in Immich with its ID still stored: deleting it automatically could remove a video the user
    /// owns if a duplicate answer named one, and the stored ID keeps discovery off it. Rerunning reaches this check again,
    /// so the original stays until the user deletes the asset for good and the next run uploads afresh.
    /// </remarks>
    private async Task<string> RequireLiveAsync(IImmichClient client, JobRecord job, string assetId, string originalId, OutputManifest manifest, CancellationToken cancellationToken)
    {
        var asset = await client.GetAssetAsync(assetId, cancellationToken);
        var finalizeAgain = hints.Command("immich finalize");
        if (asset.Id == originalId) throw new FinalizationRefusedException($"Immich returned the original of {OriginalName(job)} as its AV1.");
        if (asset.IsTrashed) throw new FinalizationWaitsException($"The uploaded AV1 ({Av1Name(job)}) is in Immich's trash. Restore it or delete it permanently, then run '{finalizeAgain}' again.");
        var expected = await ValidatedChecksumAsync(manifest, cancellationToken)
            ?? throw new FinalizationWaitsException($"{OriginalName(job)} was validated before VideoOptimiser recorded checksums, and its AV1 is no longer at {manifest.OutputPath}, so the copy in Immich ({Av1Name(job)}) can't be checked and the original stays in Immich. Put the AV1 back at that path, then run '{finalizeAgain}' again.");
        if (!string.Equals(asset.Checksum, expected, StringComparison.Ordinal))
        {
            throw new FinalizationWaitsException($"The AV1 in Immich ({Av1Name(job)}) doesn't match the one that passed validation: its checksum is {asset.Checksum}, not {expected}. The original stays in Immich. Delete {Av1Name(job)} from Immich permanently, then run '{finalizeAgain}' to upload the AV1 again.");
        }
        return asset.Id;
    }

    /// <summary>
    /// The checksum validation recorded, or, for a record written before VOPT-145, the local AV1's checksum now. Hashing the
    /// file rather than refusing keeps every job already ready finalisable without a second validation: it still proves the
    /// upload arrived whole, and leaves unproven only that the file is the one validated, which nothing proved before either.
    /// Null when neither exists: such a job can't be checked, and the caller refuses.
    /// </summary>
    private static async Task<string?> ValidatedChecksumAsync(OutputManifest manifest, CancellationToken cancellationToken) =>
        manifest.Validation?.Av1Checksum
        ?? (File.Exists(manifest.OutputPath) ? await ImmichStaging.Sha1Base64Async(manifest.OutputPath, cancellationToken) : null);

    /// <summary>
    /// F5. The job stays ready: if the original went to the Locked Folder, finalising works once it is moved out. If it was
    /// deleted, the last sentence names the copies that really survive, because cancelling the job lets cleanup delete the
    /// local download and AV1: the uploaded AV1 when Immich still has it live, otherwise every local file that exists.
    /// </summary>
    private async Task<string> OriginalNotFoundAsync(IImmichClient client, JobRecord job, CancellationToken cancellationToken)
    {
        var cancel = $"'{hints.Command($"queue drop {job.Id:N}")}'";
        var finalizeAgain = hints.Command("immich finalize");
        string? deleted = null;
        if (job.ImmichNewAssetId is { } uploaded)
        {
            try
            {
                var av1 = await client.GetAssetAsync(uploaded, cancellationToken);
                deleted = av1.IsTrashed
                    ? $"If it was deleted, its AV1 ({Av1Name(job)}) is in Immich's trash: restore it, then run {cancel}."
                    : $"If it was deleted, its AV1 ({Av1Name(job)}) is already in Immich; run {cancel} to finish.";
            }
            catch (ImmichApiException exception) when (exception.StatusCode is 400 or 404)
            {
                // Purged, or in the Locked Folder: the local files decide. Any other error propagates, so the job goes back to
                // ready on the usual resumable path rather than with advice built on a guess.
            }
        }
        var download = File.Exists(job.SourcePath);
        var output = job.OutputPath is not null && File.Exists(job.OutputPath);
        deleted ??= (download, output) switch
        {
            (true, true) => $"If it was deleted, the downloaded copy at {job.SourcePath} and its AV1 at {job.OutputPath} may be the only copies left: move them somewhere safe before running {cancel}.",
            (true, false) => $"If it was deleted, the downloaded copy at {job.SourcePath} may be the only one left: move it somewhere safe before running {cancel}.",
            (false, true) => $"If it was deleted, its AV1 at {job.OutputPath} may be the only copy left: move it somewhere safe before running {cancel}.",
            // Reached only when the AV1 was uploaded (finalise refuses a missing local AV1 otherwise) and the user removed both files.
            _ => $"If it was deleted, no copy is left on this PC and its AV1 ({Av1Name(job)}) can't be read in Immich: if it's in the Locked Folder, move it out and run '{finalizeAgain}' again; otherwise nothing is left to keep, so run {cancel}."
        };
        return $"Immich can't find the original of {OriginalName(job)}: it was deleted for good or moved to the Locked Folder. If it's in the Locked Folder, move it out and run '{finalizeAgain}' again. {deleted}";
    }
}
