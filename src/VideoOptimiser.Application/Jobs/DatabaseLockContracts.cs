namespace VideoOptimiser.Application.Jobs;

/// <summary>
/// How commands name themselves in the database's lock files. They are Application's, not the CLI's, because the queue verbs
/// must tell a run (<c>run</c> or <c>immich run</c>, which take the next video from the queue as it is) from a verb holding
/// the run lock for a moment, and from a run of an older version, which keeps the videos it started with.
/// </summary>
public static class LockHolders
{
    /// <summary>How a run names itself in the run lock, and so to a command refused by it.</summary>
    public const string Run = "video-optimiser run";
    public const string ImmichRun = "video-optimiser immich run";

    public const string QueueHold = "queue hold";
    public const string QueueNext = "queue next";
    public const string QueueDrop = "queue drop";

    /// <summary>The queue verbs take the run lock for a moment to see whether a run holds it (<see cref="IDatabaseLocks.TryHoldRun"/>); that is not a run.</summary>
    public static readonly IReadOnlySet<string> QueueVerbs = new HashSet<string>(StringComparer.Ordinal) { QueueHold, QueueNext, QueueDrop };
}

/// <summary>The run going on a database, as its lock file records it.</summary>
/// <param name="Command">How the run names itself, such as <see cref="LockHolders.Run"/>; an older version's run names itself differently.</param>
/// <param name="StartedUtc">When it took the lock.</param>
/// <param name="ProcessId">The process holding the lock, so a user can find or stop it.</param>
public sealed record LiveRunHolder(string Command, DateTimeOffset StartedUtc, int ProcessId);

/// <summary>
/// The database's lock files as use cases take them. They are OS file locks, so they live behind this port. A run holds the
/// run lock for the whole command and the finalise lock while it prepares; a finalisation holds the finalise lock. What
/// the CLI takes for a whole command, with the wording of a refusal, it still takes from <c>DatabaseLock</c> itself.
/// </summary>
public interface IDatabaseLocks
{
    /// <summary>
    /// Takes the finalise lock as a run preparing, which a finalisation that starts meanwhile waits on instead of being
    /// refused. Null while a finalisation holds it: that finalisation's jobs are live.
    /// </summary>
    IDisposable? TryHoldFinalizeWhilePreparing(string databasePath);

    /// <summary>
    /// Takes the run lock for as long as the caller holds the result, to see whether a run holds it. Null while one does
    /// (or another command is taking it), so the jobs it converts are really converting.
    /// </summary>
    /// <param name="command">How the holder names itself, such as <see cref="LockHolders.QueueDrop"/>.</param>
    IDisposable? TryHoldRun(string databasePath, string command);

    /// <summary>Whether a finalisation (not a run preparing) holds the finalise lock, read without taking it.</summary>
    bool IsFinaliseGoing(string databasePath);

    /// <summary>
    /// The run going on this database, or null when none is: read from the lock file without taking the lock. A queue verb
    /// holding it for a moment is not a run.
    /// </summary>
    LiveRunHolder? LiveRun(string databasePath);

    /// <summary>
    /// The finalise lock for a command, waiting through a run that is preparing, however long it takes. Null when a
    /// finalisation holds it.
    /// </summary>
    Task<IDisposable?> TakeFinalizeAsync(string databasePath, string command, CancellationToken cancellationToken);
}
