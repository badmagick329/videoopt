using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.TemporaryOutputs;

namespace VideoOptimiser.Application.Features.Cleanup;

/// <summary>
/// An AV1 or downloaded copy that cleanup would delete by its job's status alone but leaves, because it may be the only copy
/// of the video (<see cref="IOnlyCopyGuard"/>). <paramref name="IsAv1"/> tells the two apart for the words that name it.
/// </summary>
public sealed record KeptOnlyCopy(string Path, Guid JobId, bool IsAv1, OnlyCopyRisk Risk);

/// <param name="Kept">Files left alone because they may be the only copy. They are in neither of the deletable lists, so <see cref="ITemporaryOutputCleanupService.DeleteAsync"/> never touches them.</param>
public sealed record TemporaryOutputCleanupPlan(IReadOnlyList<AbandonedTemporaryOutput> Abandoned, IReadOnlyList<StagedImmichOriginal> StagedOriginals, IReadOnlyList<string> UnknownJobFiles, IReadOnlyList<KeptOnlyCopy> Kept)
{
    public int Count => Abandoned.Count + StagedOriginals.Count;
    public long TotalBytes => Abandoned.Sum(file => file.SizeBytes) + StagedOriginals.Sum(file => file.SizeBytes);
}

/// <param name="JobChanged">Planned files left alone because their job, read again at delete time, no longer lets cleanup delete them.</param>
public sealed record TemporaryOutputCleanupResult(int Deleted, long DeletedBytes, IReadOnlyList<string> Failures, IReadOnlyList<string> JobChanged);

public interface ITemporaryOutputCleanupService
{
    /// <summary>
    /// Finds abandoned temporary outputs and, under <c>immich.stagingDirectory</c>, staged Immich originals no open job reads.
    /// The staging folder is searched whether or not Immich is turned on: the copies stay on disk when <c>immich.baseUrl</c>
    /// is emptied. Of a job that is not completed, an AV1 or downloaded copy that may be the only copy of the video is left
    /// out of the deletable lists and reported in <see cref="TemporaryOutputCleanupPlan.Kept"/>; Immich is asked once for
    /// such a job.
    /// </summary>
    Task<TemporaryOutputCleanupPlan> FindAsync(string databasePath, ImmichSettings immich, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the plan's files, except those whose job, read again now, no longer qualifies: they are left and listed in
    /// <see cref="TemporaryOutputCleanupResult.JobChanged"/>.
    /// </summary>
    Task<TemporaryOutputCleanupResult> DeleteAsync(string databasePath, TemporaryOutputCleanupPlan plan, CancellationToken cancellationToken = default);
}
