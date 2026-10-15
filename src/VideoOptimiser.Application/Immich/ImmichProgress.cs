namespace VideoOptimiser.Application.Immich;

/// <summary>
/// What work on Immich originals tells its caller as it goes. The events carry facts, not text, so the CLI owns the wording.
/// Restaging is here because <c>run</c> does it and it can find a problem with an asset as discovery can; Immich discovery
/// adds its own events on top (<c>ImmichDiscoveryProgress</c>).
/// </summary>
public abstract record ImmichProgress
{
    // Closed to this assembly: the console switches over every event, so a stray subclass would print nothing.
    private protected ImmichProgress() { }

    /// <summary>An open job lost its staged copy and it is being downloaded again.</summary>
    public sealed record Restaging(string FileName, Guid JobId) : ImmichProgress;

    /// <param name="Problem">Null when the copy is back; otherwise why the job was failed instead.</param>
    public sealed record Restaged(string FileName, string? Problem) : ImmichProgress;

    /// <summary>A problem with an asset that needed no download to see, such as a name that cannot be staged safely.</summary>
    public sealed record AssetIssue(string FileName, string Message) : ImmichProgress;
}
