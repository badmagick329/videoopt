namespace VideoOptimiser.Domain.Jobs;

public enum JobStatus
{
    Queued = 0,
    CrfSearching = 1,
    Encoding = 2,
    Validating = 3,
    ReadyToFinalize = 4,
    Finalizing = 5,
    Completed = 6,
    Failed = 7,
    Interrupted = 8,
    Cancelled = 9,
    Staging = 10,
    Downloading = 11,
    /// <summary>Ended without converting because AV1 would not save enough. Terminal, and not a failure.</summary>
    Skipped = 12
}

/// <summary>Where a job's original came from. Stored as <c>file</c> or <c>immich</c>.</summary>
public enum JobSourceKind
{
    File = 0,
    Immich = 1
}
