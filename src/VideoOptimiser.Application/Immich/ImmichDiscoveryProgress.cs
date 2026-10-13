namespace VideoOptimiser.Application.Immich;

/// <summary>
/// What Immich discovery tells its caller as it works. The events carry facts, not text, so the CLI owns the layout and
/// every wording the user reads sits in one place. A candidate is a video downloaded so ffprobe can judge it: it is
/// announced with <see cref="CandidateDownloading"/> before the download starts, which can take minutes, and settled with
/// <see cref="CandidateJudged"/>.
/// </summary>
public abstract record ImmichDiscoveryProgress
{
    private ImmichDiscoveryProgress() { }

    /// <param name="Limit">The most videos wanted, or null for every eligible one.</param>
    /// <param name="AlreadyQueued">Open Immich jobs before the search.</param>
    /// <param name="StaleSkips">Recorded skips made under other eligibility rules; each is downloaded and judged again.</param>
    public sealed record Started(int? Limit, int AlreadyQueued, int StaleSkips) : ImmichDiscoveryProgress;

    /// <summary>An open job lost its staged copy and it is being downloaded again.</summary>
    public sealed record Restaging(string FileName, Guid JobId) : ImmichDiscoveryProgress;

    /// <param name="Problem">Null when the copy is back; otherwise why the job was failed instead.</param>
    public sealed record Restaged(string FileName, string? Problem) : ImmichDiscoveryProgress;

    public sealed record CandidateDownloading(int Number, string FileName, DateTime Taken, long? SizeBytes) : ImmichDiscoveryProgress;

    /// <param name="Detail">The short reason for a skip, or the problem of an issue.</param>
    /// <param name="NextStep">What happens to an issue next; null otherwise.</param>
    /// <param name="Found">For a queued candidate, the videos now in flight (queued earlier and now), against the limit.</param>
    public sealed record CandidateJudged(CandidateOutcome Outcome, string? Detail, string? NextStep, int Found) : ImmichDiscoveryProgress;

    /// <summary>A search result the re-check refused: Immich's filter let through an asset it should not have.</summary>
    public sealed record AssetExcluded(string FileName, string AssetId, string Reason) : ImmichDiscoveryProgress;

    /// <summary>A problem with an asset that needed no download to see, such as a name that cannot be staged safely.</summary>
    public sealed record AssetIssue(string FileName, string Message) : ImmichDiscoveryProgress;
}

public enum CandidateOutcome
{
    Queued,
    Skipped,
    Issue
}
