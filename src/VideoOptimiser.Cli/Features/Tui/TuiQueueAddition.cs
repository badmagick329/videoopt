namespace VideoOptimiser.Cli.Features.Tui;

internal enum QueueAdditionSource { WatchFolders, Immich, File }

/// <summary>Shares one busy key because overlapping discoveries can write the same Immich staging paths.</summary>
/// <param name="Paths">The files to add, in order, for <see cref="QueueAdditionSource.File"/>; null for the other sources.</param>
internal sealed record AddVideos(QueueAdditionSource Source, IReadOnlyList<string>? Paths = null, int? Limit = null, bool Force = false) : TuiEffect
{
    public const string Key = "add";
    public override string BusyKey => Key;
    public override string BusyText => Source switch
    {
        QueueAdditionSource.WatchFolders => "Scanning the watch folders.",
        QueueAdditionSource.Immich => "Adding videos from Immich.",
        _ when Paths is { Count: > 1 } => $"Adding {Paths.Count} files.",
        _ => "Adding the file."
    };

    // A record compares a list by reference; the force question's answer and the tests compare what is asked, path by path.
    public bool Equals(AddVideos? other) =>
        other is not null && Source == other.Source && Limit == other.Limit && Force == other.Force
        && (Paths ?? []).SequenceEqual(other.Paths ?? [], StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Source, Limit, Force, Paths?.Count ?? 0);
}

/// <summary>Cancels only queue addition; the conversion run and finalisation have separate stop signals.</summary>
internal sealed record CancelQueueAddition : TuiEffect;

/// <param name="ForceCandidates">
/// The files refused only by the watch roots or the rules, which force may lift; every other refusal keeps its protection, so
/// those files are never offered. Null or empty: nothing to ask.
/// </param>
internal sealed record QueueAdditionDoneMsg(AddVideos Asked, ActionResult Result, IReadOnlyList<string>? ForceCandidates = null) : TuiMsg;
