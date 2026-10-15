using System.Text.Json;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Application.Features.ImmichRun;

/// <summary>
/// Catches originals the phone uploaded again after conversion. The phone decides "already backed up" by SHA-1, so once
/// Immich purges a trashed original, a copy still on the phone uploads again as a new asset. The guard trashes that asset
/// rather than deleting it: while it sits in the trash its checksum keeps blocking the phone, and the user can still undo.
/// </summary>
public sealed class ImmichReuploadGuard(IImmichClientFactory clients, IJobRepository jobs, int batchSize = 1000) : IImmichReuploadGuard
{
    public async Task<ImmichReuploadGuardResult> CheckAsync(string databasePath, AppSettings settings, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var converted = (await jobs.ListCompletedImmichAsync(databasePath, cancellationToken))
            .Where(job => job.ImmichChecksum is not null && job.ImmichAssetId is not null)
            .ToArray();
        // A retried conversion can leave several completed jobs with one checksum; Immich is asked about each checksum once.
        var byChecksum = converted
            .GroupBy(job => job.ImmichChecksum!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        List<ImmichReuploadHit> trashed = [], restored = [];
        List<ImmichReuploadIssue> issues = [];
        if (byChecksum.Count == 0) return new ImmichReuploadGuardResult(0, trashed, restored, issues);

        var protectedAssets = await ProtectedAssetsAsync(databasePath, cancellationToken);
        var client = clients.Create(settings.Immich);
        // Each hit is reported as soon as it is known, so an error that ends the guard later still leaves the user a list
        // of what was trashed: the next run sees those assets as trashed and stays silent about them.
        void Issue(ImmichReuploadIssue issue)
        {
            issues.Add(issue);
            progress?.Report(new ScanProgress(issue.AssetId, "Issue", issue.Reason));
        }

        foreach (var batch in byChecksum.Keys.Chunk(batchSize))
        {
            var reuploads = new List<ImmichReuploadHit>();
            foreach (var check in await client.CheckUploadsAsync(batch, cancellationToken))
            {
                // Accepted: the checksum is gone from Immich. Rejected but trashed: the original is still in its trash window.
                if (!check.Rejected || check.IsTrashed || check.ExistingAssetId is not { } assetId) continue;

                var group = byChecksum[check.Checksum];
                var restoredJob = group.FirstOrDefault(job => job.ImmichAssetId == assetId);
                var job = restoredJob ?? group[^1];
                ImmichAsset asset;
                try
                {
                    asset = await client.GetAssetAsync(assetId, cancellationToken);
                }
                catch (Exception exception) when (exception is ImmichApiException { Code: "ImmichRequestFailed" } or JsonException)
                {
                    Issue(new ImmichReuploadIssue(job.Id, assetId, $"Could not read asset {assetId}, which holds a converted original's checksum: {exception.Message}"));
                    continue;
                }

                var hit = new ImmichReuploadHit(job.Id, assetId, asset.OriginalFileName, asset.LocalDateTime.DateTime);
                if (restoredJob is not null)
                {
                    // Reported only when the asset confirms it: a stale upload check must not send the user looking for a
                    // restore that never happened.
                    if (asset.IsTrashed || !string.Equals(asset.Checksum, check.Checksum, StringComparison.Ordinal)) continue;
                    restored.Add(hit);
                    progress?.Report(new ScanProgress(hit.OriginalFileName, "Restored", $"{hit.OriginalFileName} (taken {hit.TakenLocal:yyyy-MM-dd HH:mm}) is a converted original that is out of Immich's trash, so both versions exist; the guard left it alone."));
                    continue;
                }

                if (Mismatch(asset, check.Checksum, protectedAssets) is { } mismatch)
                {
                    Issue(new ImmichReuploadIssue(job.Id, assetId, $"Immich reports asset {assetId} as holding a converted original's checksum, but it {mismatch}; leaving it alone."));
                    continue;
                }

                reuploads.Add(hit);
            }

            await TrashAsync(client, reuploads, trashed, Issue, progress, cancellationToken);
        }

        return new ImmichReuploadGuardResult(byChecksum.Count, trashed, restored, issues);
    }

    /// <summary>
    /// Trashes a batch's re-uploads in one call. When Immich refuses that call (one asset vanished or changed since it was
    /// read), each asset is retried alone so the others are still trashed. Any other failure ends the guard.
    /// </summary>
    private static async Task TrashAsync(IImmichClient client, List<ImmichReuploadHit> reuploads, List<ImmichReuploadHit> trashed, Action<ImmichReuploadIssue> issue, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        if (reuploads.Count == 0) return;
        try
        {
            await client.TrashAsync(reuploads.Select(hit => hit.AssetId).ToArray(), cancellationToken);
            foreach (var hit in reuploads) Trashed(hit);
            return;
        }
        catch (ImmichApiException exception) when (exception.Code == "ImmichRequestFailed" && reuploads.Count > 1)
        {
            // Fall through and trash one asset at a time.
        }

        foreach (var hit in reuploads)
        {
            try
            {
                await client.TrashAsync([hit.AssetId], cancellationToken);
                Trashed(hit);
            }
            catch (ImmichApiException exception) when (exception.Code == "ImmichRequestFailed")
            {
                issue(new ImmichReuploadIssue(hit.JobId, hit.AssetId, $"Could not trash the re-uploaded {hit.OriginalFileName}: {exception.Message}"));
            }
        }

        void Trashed(ImmichReuploadHit hit)
        {
            trashed.Add(hit);
            progress?.Report(new ScanProgress(hit.OriginalFileName, "Trashed", $"{hit.OriginalFileName} (taken {hit.TakenLocal:yyyy-MM-dd HH:mm}) is a converted original the phone uploaded again; it is back in Immich's trash. Delete it from the phone."));
        }
    }

    /// <summary>Every asset a job started from or finalisation uploaded, whatever the job's state; none of them is a re-upload.</summary>
    private async Task<HashSet<string>> ProtectedAssetsAsync(string databasePath, CancellationToken cancellationToken)
    {
        var assets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var terminal in new[] { false, true })
        {
            foreach (var job in await jobs.ListAsync(databasePath, terminal, cancellationToken))
            {
                if (job.ImmichAssetId is { } original) assets.Add(original);
                if (job.ImmichNewAssetId is { } replacement) assets.Add(replacement);
            }
        }

        return assets;
    }

    /// <summary>
    /// Why an asset the upload check named must not be trashed, or null for a re-upload. The upload check is not trusted
    /// alone because trashing the wrong asset removes a video the user kept: the asset itself must carry the checksum and
    /// be a live library video that no job started from or created.
    /// </summary>
    private static string? Mismatch(ImmichAsset asset, string checksum, HashSet<string> protectedAssets)
    {
        if (!string.Equals(asset.Checksum, checksum, StringComparison.Ordinal)) return "has a different checksum";
        if (protectedAssets.Contains(asset.Id)) return "belongs to another job";
        if (!string.Equals(asset.Type, ImmichValues.Video, StringComparison.Ordinal)) return $"is a {asset.Type}, not a video";
        if (asset.IsTrashed) return "is already in the trash";
        if (asset.Visibility is not (ImmichValues.Timeline or ImmichValues.Archive)) return $"has visibility {asset.Visibility}";
        if (asset.LibraryId is not null) return "belongs to an external library";
        return null;
    }
}
