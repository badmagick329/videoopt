using VideoOptimiser.Application.Immich;

namespace VideoOptimiser.Application.Features.ImmichRun;

/// <summary>
/// What Immich discovery tells its caller as it works. The events carry facts, not text, so the CLI owns the layout and
/// every wording the user reads sits in one place. A candidate is a video downloaded so ffprobe can judge it: it is
/// announced with <see cref="CandidateDownloading"/> before the download starts, which can take minutes, and settled with
/// <see cref="CandidateJudged"/>. Discovery restages first, so its events include <see cref="ImmichProgress.Restaging"/>
/// and <see cref="ImmichProgress.Restaged"/>, which <c>run</c> shares.
/// </summary>
public abstract record ImmichDiscoveryProgress : ImmichProgress
{
    private ImmichDiscoveryProgress() { }

    /// <param name="Limit">The most videos wanted, or null for every eligible one.</param>
    /// <param name="AlreadyQueued">Open Immich jobs before the search.</param>
    /// <param name="StaleSkips">Recorded skips made under other eligibility rules; each is downloaded and judged again.</param>
    /// <param name="RuleCount">Configured eligibility rules; with one, a rule's number says nothing and is left out.</param>
    public sealed record Started(int? Limit, int AlreadyQueued, int StaleSkips, int RuleCount) : ImmichDiscoveryProgress;

    public sealed record CandidateDownloading(int Number, string FileName, DateTime Taken) : ImmichDiscoveryProgress;

    /// <param name="Detail">The short reason for a skip, or the problem of an issue.</param>
    /// <param name="NextStep">What happens to an issue next; null otherwise.</param>
    /// <param name="Found">For a queued candidate, the videos now in flight (queued earlier and now), against the limit.</param>
    /// <param name="Facts">What ffprobe and the download showed; null for an issue, which got no verdict on them.</param>
    /// <param name="Rule">For a queued candidate, the number of the first eligibility rule it fits.</param>
    public sealed record CandidateJudged(CandidateOutcome Outcome, string? Detail, string? NextStep, int Found, CandidateFacts? Facts = null, int? Rule = null) : ImmichDiscoveryProgress;

    /// <summary>A search result the re-check refused: Immich's filter let through an asset it should not have.</summary>
    public sealed record AssetExcluded(string FileName, string AssetId, string Reason) : ImmichDiscoveryProgress;
}

/// <summary>
/// What a judged candidate looked like, as measured: the user reads the verdict against these, so they travel as values and
/// the CLI decides how to word them. Dimensions and bitrate are null when ffprobe did not report them.
/// </summary>
public sealed record CandidateFacts(string Codec, int? Width, int? Height, long? VideoBitsPerSecond, long SizeBytes);

public enum CandidateOutcome
{
    Queued,
    Skipped,
    Issue
}
