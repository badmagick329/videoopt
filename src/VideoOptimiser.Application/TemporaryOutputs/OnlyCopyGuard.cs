using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.TemporaryOutputs;

public enum OnlyCopyReason
{
    /// <summary>A folder video's original is missing while its AV1 exists.</summary>
    OriginalMissing,
    ImmichNotConfigured,
    /// <summary>Immich failed to answer; <see cref="OnlyCopyRisk.Detail"/> is what it said.</summary>
    ImmichUnavailable,
    /// <summary>The original is deleted from Immich, in its trash, or replaced by other bytes.</summary>
    ImmichOriginalGone
}

/// <param name="Detail">What Immich said, for <see cref="OnlyCopyReason.ImmichUnavailable"/>.</param>
public sealed record OnlyCopyRisk(OnlyCopyReason Reason, string? Detail = null);

/// <summary>
/// The rule that keeps <c>cleanup</c> from deleting the only copy of a video. Cleanup deletes the AV1 and the downloaded copy
/// of any job that is not completed, so both <c>queue drop</c> (which hands a job to cleanup) and <c>cleanup</c> itself ask
/// this first. It lives here, not in either slice, so the two cannot disagree about what counts as the only copy.
/// </summary>
public interface IOnlyCopyGuard
{
    /// <returns>Why the AV1 or downloaded copy of <paramref name="job"/> may be the only copy of the video, or null when deleting them is safe.</returns>
    Task<OnlyCopyRisk?> CheckAsync(ImmichSettings immich, JobRecord job, CancellationToken cancellationToken);
}

/// <summary>
/// A folder video is at risk when its AV1 exists and its original is missing. An Immich video is at risk when no AV1 is
/// uploaded to Immich yet, something is left on this PC, and Immich cannot show the original is still there: it is not
/// configured, it fails to answer, or the original is deleted (Immich answers 400 or 404), in the trash, which empties, or
/// replaced by other bytes. Immich is asked here, so a check it cannot answer is a risk: an unanswered question is not "safe".
/// </summary>
public sealed class OnlyCopyGuard(IImmichClientFactory immichClients) : IOnlyCopyGuard
{
    public async Task<OnlyCopyRisk?> CheckAsync(ImmichSettings immich, JobRecord job, CancellationToken cancellationToken)
    {
        var av1 = job.OutputPath is { } output && File.Exists(output) ? output : null;
        if (job.SourceKind == JobSourceKind.File)
        {
            return av1 is not null && !File.Exists(job.SourcePath) ? new OnlyCopyRisk(OnlyCopyReason.OriginalMissing) : null;
        }
        if (job.ImmichNewAssetId is not null || (av1 is null && !File.Exists(job.SourcePath))) return null;

        if (!immich.IsConfigured) return new OnlyCopyRisk(OnlyCopyReason.ImmichNotConfigured);
        var gone = false;
        try
        {
            var asset = await immichClients.Create(immich).GetAssetAsync(job.ImmichAssetId!, cancellationToken);
            gone = asset.IsTrashed || !string.Equals(asset.Checksum, job.ImmichChecksum, StringComparison.Ordinal);
        }
        catch (ImmichApiException exception) when (exception.StatusCode is 400 or 404)
        {
            gone = true;
        }
        catch (ImmichApiException exception)
        {
            return new OnlyCopyRisk(OnlyCopyReason.ImmichUnavailable, exception.Message);
        }
        return gone ? new OnlyCopyRisk(OnlyCopyReason.ImmichOriginalGone) : null;
    }
}
