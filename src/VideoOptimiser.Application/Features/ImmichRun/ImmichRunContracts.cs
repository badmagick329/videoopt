using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.ImmichRun;

/// <summary>
/// What one Immich discovery run did. <see cref="Excluded"/> counts search results the client-side re-check rejected, which
/// means Immich's filter let through an asset it should not have. <see cref="Ineligible"/> counts assets rejected without a
/// download (extension or size); <see cref="Skipped"/> counts those downloaded, probed and recorded as Immich skips, and
/// <see cref="SkipReasons"/> tallies them by the reason <see cref="Eligibility.EligibilityEvaluator.DescribeMismatch"/> gave, most common first.
/// <see cref="AlreadyQueued"/>, <see cref="Held"/> and
/// <see cref="ReadyToFinalize"/> count every open Immich job as <c>queue add</c> counts files, not only those the search
/// reached.
/// </summary>
public sealed record ImmichDiscoveryResult(
    IReadOnlyList<JobRecord> Queued,
    int AlreadyQueued,
    int Held,
    int ReadyToFinalize,
    int AlreadyConverted,
    int PreviouslyFailed,
    int PreviouslySkipped,
    int Excluded,
    int Ineligible,
    int Skipped,
    int Issues,
    IReadOnlyList<SkipReasonCount> SkipReasons)
{
    /// <summary>Videos downloaded and judged against the rules this run: those queued and those skipped. Issues were not judged.</summary>
    public int Checked => Queued.Count + Skipped;
}

public sealed record SkipReasonCount(string Reason, int Count);

public interface IImmichDiscoveryService
{
    /// <summary>
    /// Stages and queues every eligible Immich video without an open, completed or settled failed job. With a
    /// <paramref name="limit"/>, it stops once that many new jobs are queued; open jobs do not count. Assets that need a
    /// download to judge are downloaded under <see cref="ImmichSettings.StagingDirectory"/>.
    /// </summary>
    Task<ImmichDiscoveryResult> DiscoverAsync(string databasePath, AppSettings settings, int? limit, IProgress<ImmichProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// An asset holding a converted original's checksum, as the user would look for it on the phone: the file name and the
/// capture time Immich shows, in the asset's local time.
/// </summary>
public sealed record ImmichReuploadHit(Guid JobId, string AssetId, string OriginalFileName, DateTime TakenLocal);

/// <summary>An asset Immich's upload check named that the guard left alone because the asset itself did not confirm the match.</summary>
public sealed record ImmichReuploadIssue(Guid JobId, string AssetId, string Reason);

/// <summary>
/// What one re-upload guard run did. <see cref="Trashed"/> are originals the phone uploaded again after conversion, now back
/// in Immich's trash; the user still has to delete them from the phone. <see cref="Restored"/> are originals the user took
/// back out of the trash, so both versions exist; the guard never trashes these.
/// </summary>
public sealed record ImmichReuploadGuardResult(
    int Checked,
    IReadOnlyList<ImmichReuploadHit> Trashed,
    IReadOnlyList<ImmichReuploadHit> Restored,
    IReadOnlyList<ImmichReuploadIssue> Issues);

public interface IImmichReuploadGuard
{
    /// <summary>
    /// Asks Immich about the checksum of every converted original and trashes each live asset that holds one under a new ID.
    /// Runs before discovery in every <c>immich run</c> and <c>queue add immich</c>. <paramref name="progress"/> hears each hit and issue as it is found,
    /// so they reach the user even when a later error ends the guard.
    /// </summary>
    Task<ImmichReuploadGuardResult> CheckAsync(string databasePath, AppSettings settings, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>What <c>queue add immich</c> did, and what <c>immich run</c> does before it converts the queue.</summary>
public sealed record ImmichQueueAddition(ImmichReuploadGuardResult Guard, ImmichDiscoveryResult Discovery);

public interface IImmichRunService
{
    /// <summary>
    /// Runs the re-upload guard, then Immich discovery, which queues up to <paramref name="limit"/> new videos. Converting
    /// them is left to the caller: <c>immich run</c> converts the whole queue as <c>run</c> does.
    /// </summary>
    Task<ImmichQueueAddition> AddToQueueAsync(string databasePath, AppSettings settings, int? limit, IProgress<ScanProgress>? guardProgress = null, IProgress<ImmichProgress>? discoveryProgress = null, CancellationToken cancellationToken = default);
}
