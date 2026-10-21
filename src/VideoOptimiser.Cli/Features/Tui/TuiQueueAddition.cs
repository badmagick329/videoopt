namespace VideoOptimiser.Cli.Features.Tui;

internal enum QueueAdditionSource { WatchFolders, Immich, File }

/// <summary>Shares one busy key because overlapping discoveries can write the same Immich staging paths.</summary>
internal sealed record AddVideos(QueueAdditionSource Source, string? Path = null, int? Limit = null, bool Force = false) : TuiEffect
{
    public const string Key = "add";
    public override string BusyKey => Key;
    public override string BusyText => Source switch
    {
        QueueAdditionSource.WatchFolders => "Scanning the watch folders.",
        QueueAdditionSource.Immich => "Adding videos from Immich.",
        _ => "Adding the file."
    };
}

/// <summary>Cancels only queue addition; the conversion run and finalisation have separate stop signals.</summary>
internal sealed record CancelQueueAddition : TuiEffect;

/// <summary>A rules refusal may offer force; other refusals must keep their protections.</summary>
internal sealed record QueueAdditionDoneMsg(AddVideos Asked, ActionResult Result, bool OfferForce = false) : TuiMsg;
