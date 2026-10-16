using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Immich;

/// <summary>Earlier Immich jobs, read once per run because every repository call re-runs the database initialiser.</summary>
public sealed class ImmichHistory
{
    private readonly HashSet<string> _openAssets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _convertedAssets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _convertedChecksums = new(StringComparer.Ordinal);
    private readonly HashSet<string> _replacements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JobRecord> _latestTerminal = new(StringComparer.Ordinal);

    /// <summary>Assets with an open job, held ones included: a held video is parked, so its asset is not queued a second time.</summary>
    public IReadOnlySet<string> OpenAssets => _openAssets;

    /// <summary>The open Immich jobs by what happens to them next, as <c>queue add</c> counts files: held, ready to finalise, or the rest (a run takes those).</summary>
    public int Held { get; private set; }
    public int ReadyToFinalize { get; private set; }
    public int Waiting { get; private set; }

    public static async Task<ImmichHistory> LoadAsync(IJobRepository jobs, string databasePath, CancellationToken cancellationToken)
    {
        var history = new ImmichHistory();
        foreach (var job in await jobs.ListAsync(databasePath, terminal: false, cancellationToken))
        {
            if (job.ImmichAssetId is { } assetId)
            {
                history._openAssets.Add(assetId);
                if (job.Held) history.Held++;
                else if (job.Status == JobStatus.ReadyToFinalize || FinalizationRules.IsInterruptedImmich(job)) history.ReadyToFinalize++;
                else history.Waiting++;
            }
            if (job.ImmichNewAssetId is { } replacement) history._replacements.Add(replacement);
        }

        foreach (var job in await jobs.ListAsync(databasePath, terminal: true, cancellationToken))
        {
            if (job.ImmichNewAssetId is { } replacement) history._replacements.Add(replacement);
            if (job.ImmichAssetId is not { } assetId) continue;
            if (job.Status == JobStatus.Completed)
            {
                history._convertedAssets.Add(assetId);
                if (job.ImmichChecksum is { } checksum) history._convertedChecksums.Add(checksum);
            }

            if (!history._latestTerminal.TryGetValue(assetId, out var latest) || job.CreatedUtc > latest.CreatedUtc) history._latestTerminal[assetId] = job;
        }

        return history;
    }

    /// <summary>
    /// A completed job converted this asset, or one with the same checksum: an original the phone uploaded again after
    /// conversion, which the re-upload guard trashes. Or it is an AV1 asset that finalisation uploaded, which is just as
    /// old and on the timeline. Discovery must never download or convert any of these.
    /// </summary>
    public bool IsConverted(ImmichAsset asset) =>
        _convertedAssets.Contains(asset.Id) || _convertedChecksums.Contains(asset.Checksum) || _replacements.Contains(asset.Id);

    /// <summary>
    /// The asset's latest job was skipped as not worth converting, under the quality settings in force now. Changing them
    /// makes the asset a candidate again; only then could AV1 pass.
    /// </summary>
    public bool IsSettledSkip(ImmichAsset asset, string qualityFingerprint) =>
        _latestTerminal.TryGetValue(asset.Id, out var job) &&
        job.Status == JobStatus.Skipped &&
        string.Equals(job.ImmichChecksum, asset.Checksum, StringComparison.Ordinal) &&
        string.Equals(job.QualityFingerprint, qualityFingerprint, StringComparison.Ordinal);

    /// <summary>
    /// The asset's latest job failed or was cancelled while the asset was unchanged, so converting it again would repeat
    /// that outcome; only <c>queue retry</c> requeues it, as for a settled original.
    /// </summary>
    public bool IsSettledFailure(ImmichAsset asset) =>
        _latestTerminal.TryGetValue(asset.Id, out var job) &&
        job.Status is JobStatus.Failed or JobStatus.Cancelled &&
        string.Equals(job.ImmichChecksum, asset.Checksum, StringComparison.Ordinal) &&
        !FailureCategories.Requeued.Contains(job.FailureCategory ?? string.Empty);

    /// <summary>
    /// The asset's latest job was rejected by the user while the asset was unchanged: they looked at that AV1 and did not want
    /// it, so converting the asset again would only make it again. Counted apart from <see cref="IsSettledFailure"/> because
    /// nothing failed. Only <c>queue retry</c> requeues it, or a change to the asset.
    /// </summary>
    public bool IsSettledRejection(ImmichAsset asset) =>
        _latestTerminal.TryGetValue(asset.Id, out var job) &&
        job.Status == JobStatus.Rejected &&
        string.Equals(job.ImmichChecksum, asset.Checksum, StringComparison.Ordinal);
}

/// <summary>Shared by discovery and calibrate, whose Immich population must be the assets this discovery would judge.</summary>
public static class ImmichExclusion
{
    /// <summary>
    /// Why an asset the search returned must not be converted, or null for a candidate. This repeats the search filter on
    /// purpose: a wrong match ends with an original in Immich's trash, so the server's filter is not trusted alone.
    /// </summary>
    public static string? Reason(ImmichAsset asset, DateTimeOffset cutoff, bool skipFavorites)
    {
        if (!string.Equals(asset.Type, ImmichValues.Video, StringComparison.Ordinal)) return $"is a {asset.Type}, not a video";
        if (asset.FileCreatedAt >= cutoff) return "is newer than the age gate";
        if (skipFavorites && asset.IsFavorite) return "is a favourite";
        if (asset.IsTrashed) return "is in the trash";
        if (asset.Visibility is not (ImmichValues.Timeline or ImmichValues.Archive)) return $"has visibility {asset.Visibility}";
        if (asset.LibraryId is not null) return "belongs to an external library";
        return null;
    }
}
