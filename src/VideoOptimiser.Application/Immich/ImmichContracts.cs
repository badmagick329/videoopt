using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Immich;

/// <summary>
/// Gives an open Immich job back the staged original it lost. <c>run</c> does it for the Immich videos it is about to
/// convert, whichever command started it.
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
}
