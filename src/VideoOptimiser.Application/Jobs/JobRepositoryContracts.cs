using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Jobs;

public interface IJobRepository
{
    Task<JobRecord?> FindOpenBySourceAsync(string databasePath, string sourcePath, CancellationToken cancellationToken = default);
    Task<JobRecord?> GetAsync(string databasePath, Guid id, CancellationToken cancellationToken = default);
    /// <summary>Inserts the job unless its original (path, or Immich asset) already has an open one: then <see cref="OpenJobExistsException"/>.</summary>
    Task<JobRecord> CreateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
    /// <summary>
    /// Writes every column but the queue verbs' (<see cref="JobRecord.Held"/>, <see cref="JobRecord.Priority"/>), if the row
    /// is still at <see cref="JobRecord.RowVersion"/> and not held; otherwise <see cref="JobChangedException"/>.
    /// </summary>
    Task UpdateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
    /// <summary><c>queue hold</c> and <c>release</c>. False when the row changed since <paramref name="job"/> was read.</summary>
    Task<bool> SetHeldAsync(string databasePath, JobRecord job, bool held, CancellationToken cancellationToken = default);
    /// <summary><c>queue next</c>: above every open job's priority, and released. False when the row changed since it was read.</summary>
    Task<bool> RunNextAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
    /// <summary>
    /// <c>queue drop</c>: <see cref="JobStatus.Cancelled"/> and released, held or not, keeping <see cref="JobRecord.ResumeStatus"/>
    /// so cleanup still recognises an interrupted finalisation. False when the row changed since it was read.
    /// </summary>
    Task<bool> DropAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
    /// <summary>
    /// <c>queue reject</c>: <see cref="JobStatus.Rejected"/>, released and completed now. The write comes before the AV1 is
    /// deleted, so a crash between the two leaves a rejected job whose files <c>cleanup</c> deletes. False when the row
    /// changed since it was read.
    /// </summary>
    Task<bool> RejectAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRecord>> ListAsync(string databasePath, bool terminal, CancellationToken cancellationToken = default);
    /// <summary>
    /// Marks jobs a dead process left mid-stage <see cref="JobStatus.Interrupted"/>, so they resume from that stage. Only the
    /// holder of the run lock may call it. <paramref name="includeFinalizing"/> is false while a finalisation in another
    /// process holds its lock: that <see cref="JobStatus.Finalizing"/> job is live, not left behind.
    /// </summary>
    Task MarkActiveJobsInterruptedAsync(string databasePath, bool includeFinalizing, CancellationToken cancellationToken = default);

    /// <summary>The newest job for the asset that is not completed, failed, cancelled, skipped or rejected.</summary>
    Task<JobRecord?> FindOpenByImmichAssetAsync(string databasePath, string assetId, CancellationToken cancellationToken = default);

    /// <summary>Completed Immich jobs, oldest first. The re-upload guard looks for their original checksums coming back.</summary>
    Task<IReadOnlyList<JobRecord>> ListCompletedImmichAsync(string databasePath, CancellationToken cancellationToken = default);

    /// <summary>Every recorded skip, keyed by asset ID. Discovery reads them once per run and checks each with <see cref="ImmichSkipRecord.IsCurrent"/>.</summary>
    Task<IReadOnlyDictionary<string, ImmichSkipRecord>> ListImmichSkipsAsync(string databasePath, CancellationToken cancellationToken = default);

    /// <summary>Records or replaces the skip for <see cref="ImmichSkipRecord.AssetId"/>.</summary>
    Task PutImmichSkipAsync(string databasePath, ImmichSkipRecord skip, CancellationToken cancellationToken = default);
}

/// <summary>
/// An Immich asset that discovery downloaded and found ineligible, kept so later runs skip it without downloading it again.
/// <see cref="RulesFingerprint"/> identifies the eligibility settings that rejected it, so changing the rules lets the
/// asset be checked again. <see cref="VideoFacts"/> is null when the asset was ruled out without a download, and on
/// skips recorded before migration 8.
/// </summary>
public sealed record ImmichSkipRecord(string AssetId, string Checksum, string RulesFingerprint, string Reason, DateTimeOffset RecordedUtc, VideoFacts? VideoFacts = null)
{
    /// <summary>False once the asset's file or the eligibility rules have changed since the skip was recorded.</summary>
    public bool IsCurrent(string checksum, string rulesFingerprint) =>
        string.Equals(Checksum, checksum, StringComparison.Ordinal) && string.Equals(RulesFingerprint, rulesFingerprint, StringComparison.Ordinal);
}
