using VideoOptimiser.Application.TemporaryOutputs;

namespace VideoOptimiser.Application.Features.Cleanup;

public sealed record TemporaryOutputCleanupPlan(IReadOnlyList<AbandonedTemporaryOutput> Abandoned, IReadOnlyList<StagedImmichOriginal> StagedOriginals, IReadOnlyList<string> UnknownJobFiles)
{
    public int Count => Abandoned.Count + StagedOriginals.Count;
    public long TotalBytes => Abandoned.Sum(file => file.SizeBytes) + StagedOriginals.Sum(file => file.SizeBytes);
}

public sealed record TemporaryOutputCleanupResult(int Deleted, long DeletedBytes, IReadOnlyList<string> Failures);

public interface ITemporaryOutputCleanupService
{
    /// <summary>
    /// Finds abandoned temporary outputs and, under <paramref name="immichStagingDirectory"/> (empty for none), staged Immich
    /// originals no open job reads. Callers pass the configured staging folder whether or not Immich is turned on: the copies
    /// stay on disk when <c>immich.baseUrl</c> is emptied.
    /// </summary>
    Task<TemporaryOutputCleanupPlan> FindAsync(string databasePath, string immichStagingDirectory, CancellationToken cancellationToken = default);
    TemporaryOutputCleanupResult Delete(TemporaryOutputCleanupPlan plan);
}
