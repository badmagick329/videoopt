using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.ImmichRun;

/// <summary>
/// What one Immich discovery run did. <see cref="Excluded"/> counts search results the client-side re-check rejected, which
/// means Immich's filter let through an asset it should not have. <see cref="Ineligible"/> counts assets rejected without a
/// download (extension or size); <see cref="Skipped"/> counts those downloaded, probed and recorded as Immich skips, and
/// <see cref="SkipReasons"/> tallies them by the reason <see cref="Eligibility.EligibilityEvaluator.DescribeMismatch"/> gave, most common first.
/// <see cref="Restaged"/> counts open jobs whose missing staged original was downloaded again.
/// </summary>
public sealed record ImmichDiscoveryResult(
    IReadOnlyList<JobRecord> Queued,
    int Restaged,
    int AlreadyQueued,
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
    /// <paramref name="limit"/>, it stops once that many Immich jobs are open, counting those already open. Assets that need
    /// a download to judge are downloaded under <see cref="ImmichSettings.StagingDirectory"/>.
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
    /// Runs before discovery in every <c>immich run</c>. <paramref name="progress"/> hears each hit and issue as it is found,
    /// so they reach the user even when a later error ends the guard.
    /// </summary>
    Task<ImmichReuploadGuardResult> CheckAsync(string databasePath, AppSettings settings, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// What <c>immich run</c> did before processing. <see cref="Queue"/> holds the open Immich jobs to process; with
/// <c>--limit</c> at most that many, and with <c>--first</c> at most one.
/// </summary>
public sealed record ImmichRunPreparation(ImmichReuploadGuardResult Guard, ImmichDiscoveryResult Discovery, QueuePreparationResult Queue);

public interface IImmichRunService
{
    /// <summary>
    /// Runs the re-upload guard, then Immich discovery, then prepares the open Immich jobs for processing. Processing is left
    /// to the caller, which owns the remote worker's lifecycle.
    /// </summary>
    Task<ImmichRunPreparation> PrepareAsync(string databasePath, AppSettings settings, int? limit, IProgress<ScanProgress>? guardProgress = null, IProgress<ImmichProgress>? discoveryProgress = null, bool modeOverridden = false, CancellationToken cancellationToken = default);
}
