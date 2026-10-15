using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Immich;

/// <summary>
/// Gives an open Immich job back the staged original it lost. Both <c>run</c> and <c>immich run</c> do it before they
/// convert, so it belongs to neither.
/// </summary>
public interface IImmichRestaging
{
    /// <summary>
    /// Downloads again the staged original of each of <paramref name="jobs"/> that lost it, for <c>run</c> before it converts
    /// them; each is a queued, unheld Immich job the run is about to take. A job whose bytes Immich no longer returns fails as <c>SourceMissing</c> and is in <see cref="ImmichRestageResult.Failed"/>.
    /// When Immich can't be used (unreachable, a missing or rejected key, a server error), restaging stops and
    /// <see cref="ImmichRestageResult.Unusable"/> says why.
    /// </summary>
    Task<ImmichRestageResult> RestageMissingAsync(string databasePath, AppSettings settings, IReadOnlyCollection<JobRecord> jobs, IProgress<ImmichProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The restaging itself, with the caller's <paramref name="client"/>: Immich discovery has made one for its search and
    /// restages through it, in the same pass. Unlike <see cref="RestageMissingAsync"/> it lets an Immich error that is not
    /// about one asset end the caller's run. Returns how many copies came back; each job it failed lands in <paramref name="failed"/>.
    /// </summary>
    /// <param name="failed">Receives each job marked failed, as it is.</param>
    Task<int> RestageAsync(IImmichClient client, string databasePath, AppSettings settings, IEnumerable<JobRecord> candidates, List<JobRecord> failed, IProgress<ImmichProgress>? progress, CancellationToken cancellationToken);
}
