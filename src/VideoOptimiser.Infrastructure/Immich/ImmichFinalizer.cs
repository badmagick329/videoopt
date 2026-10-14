using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Infrastructure.Jobs;

namespace VideoOptimiser.Infrastructure.Immich;

/// <summary>
/// Replaces an Immich original with its AV1: uploads the output as a new asset, copies what Immich keeps beside the file
/// (description, rating, albums, favourite, shared links, stack, sidecar, tags), and only then moves the original to
/// Immich's trash. Swapping the file inside the original asset is not an option: Immich's integrity check flags a file
/// that no longer matches its checksum, and a changed checksum makes the phone upload the original again.
/// Every step before the trash can run again, so any failure leaves the original live and the job resumable.
/// </summary>
public sealed class ImmichFinalizer(IImmichClientFactory clients, IJobRepository jobs, IOutputManifestStore manifests)
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
            throw new FinalizationWaitsException(await OriginalNotFoundAsync(client, job, originalId, cancellationToken));
        }
        if (!string.Equals(original.Checksum, checksum, StringComparison.Ordinal))
        {
            throw new FinalizationRefusedException($"Original changed in Immich: asset {originalId} no longer holds the file that was converted.");
        }

        string replacement;
        if (original.IsTrashed)
        {
            // Most likely an earlier run trashed it and stopped before marking the job completed, but the user may have
            // trashed it by hand before the copy finished, so the copy steps run again below.
            if (job.ImmichNewAssetId is not { } replaced) throw new FinalizationWaitsException($"The original, asset {originalId}, is in Immich's trash. Restore it, then run 'video-optimiser immich finalize' again.");
            replacement = await RequireLiveAsync(client, replaced, originalId, cancellationToken);
        }
        else
        {
            replacement = await ReplacementAsync(client, databasePath, job, original, manifest.OutputPath, cancellationToken);
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
        return $"New asset {replacement}; the original is in Immich's trash.";
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
    private async Task<string> ReplacementAsync(IImmichClient client, string databasePath, JobRecord job, ImmichAsset original, string outputPath, CancellationToken cancellationToken)
    {
        if (job.ImmichNewAssetId is { } stored)
        {
            try
            {
                return await RequireLiveAsync(client, stored, original.Id, cancellationToken);
            }
            catch (ImmichApiException exception) when (exception.Code == "ImmichRequestFailed")
            {
                // Unreadable, most likely purged. Uploading again is safe: if it still exists, Immich answers with its ID.
            }
        }

        var fileName = Path.ChangeExtension(original.OriginalFileName, Path.GetExtension(outputPath));
        var upload = await client.UploadAsync(new ImmichUpload(outputPath, fileName, original.FileCreatedAt, original.FileModifiedAt, original.IsFavorite), cancellationToken: cancellationToken);
        job.ImmichNewAssetId = upload.AssetId;
        await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
        // A duplicate answer names whichever asset already holds these bytes, which may be in the trash.
        return upload.IsDuplicate ? await RequireLiveAsync(client, upload.AssetId, original.Id, cancellationToken) : upload.AssetId;
    }

    private static async Task<string> RequireLiveAsync(IImmichClient client, string assetId, string originalId, CancellationToken cancellationToken)
    {
        var asset = await client.GetAssetAsync(assetId, cancellationToken);
        if (asset.Id == originalId) throw new FinalizationRefusedException($"Immich returned the original, asset {originalId}, as the AV1.");
        if (asset.IsTrashed) throw new FinalizationWaitsException($"The uploaded AV1, asset {assetId}, is in Immich's trash. Restore it or delete it permanently, then run 'video-optimiser immich finalize' again.");
        return asset.Id;
    }

    /// <summary>
    /// F5. The job stays ready: if the original went to the Locked Folder, finalising works once it is moved out. If it was
    /// deleted, the last sentence names the copies that really survive, because cancelling the job lets cleanup delete the
    /// local download and AV1: the uploaded AV1 when Immich still has it live, otherwise every local file that exists.
    /// </summary>
    private static async Task<string> OriginalNotFoundAsync(IImmichClient client, JobRecord job, string originalId, CancellationToken cancellationToken)
    {
        var cancel = $"'video-optimiser queue cancel {job.Id:N}'";
        string? deleted = null;
        if (job.ImmichNewAssetId is { } uploaded)
        {
            try
            {
                var av1 = await client.GetAssetAsync(uploaded, cancellationToken);
                deleted = av1.IsTrashed
                    ? $"If it was deleted, its AV1, asset {uploaded}, is in Immich's trash: restore it, then run {cancel}."
                    : $"If it was deleted, its AV1 is already in Immich as asset {uploaded}; run {cancel} to finish.";
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
            _ => $"If it was deleted, no copy is left on this PC and its AV1, asset {job.ImmichNewAssetId}, can't be read in Immich: if it's in the Locked Folder, move it out and run 'video-optimiser immich finalize' again; otherwise nothing is left to keep, so run {cancel}."
        };
        return $"Immich can't find the original, asset {originalId}: it was deleted for good or moved to the Locked Folder. If it's in the Locked Folder, move it out and run 'video-optimiser immich finalize' again. {deleted}";
    }
}
