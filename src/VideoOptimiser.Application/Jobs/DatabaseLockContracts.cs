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

    /// <summary>A run with no terminal, started by the hidden launcher (later the TUI): <c>run stop</c> reaches it through the requests row.</summary>
    public const string BackgroundRun = "video-optimiser background run";

    /// <summary>Every name a run (not a queue verb) takes the run lock under in this version: each takes the next video from the queue as it is then.</summary>
    public static bool IsRun(string command) => command is Run or ImmichRun or BackgroundRun;

    public const string QueueHold = "queue hold";
    public const string QueueNext = "queue next";
    public const string QueueDrop = "queue drop";

    /// <summary>
    /// How <c>queue reject</c> names itself in the finalise lock. It never takes the run lock (a ready video is not
    /// converting), so it is not in <see cref="QueueVerbs"/>.
    /// </summary>
    public const string QueueReject = "queue reject";

    /// <summary>The queue verbs take the run lock for a moment to see whether a run holds it (<see cref="IDatabaseLocks.TryHoldRun"/>); that is not a run.</summary>
    public static readonly IReadOnlySet<string> QueueVerbs = new HashSet<string>(StringComparer.Ordinal) { QueueHold, QueueNext, QueueDrop };
}

/// <summary>The run going on a database, as its lock file records it.</summary>
/// <param name="Command">How the run names itself, such as <see cref="LockHolders.Run"/>; an older version's run names itself differently.</param>
/// <param name="StartedUtc">When it took the lock.</param>
/// <param name="ProcessId">The process holding the lock, so a user can find or stop it.</param>
public sealed record LiveRunHolder(string Command, DateTimeOffset StartedUtc, int ProcessId)
{
    /// <summary>
    /// The one test for how this run is stopped, shared by <c>run stop</c> and every refusal that says how to stop it. A
    /// background run has no window and reads the requests row (<c>run stop</c>, the TUI's <c>x</c> and <c>X</c>); any other
    /// run never reads that row, so only Ctrl+C in its own window stops it.
    /// </summary>
    public bool IsBackground => Command == LockHolders.BackgroundRun;
}

/// <summary>The command holding the finalise lock, as its lock file records it, so a refused command can say who has it.</summary>
/// <param name="Command">How the holder names itself, such as <c>finalize</c> or <see cref="LockHolders.QueueReject"/>.</param>
/// <param name="StartedUtc">When it took the lock.</param>
/// <param name="ProcessId">The process holding the lock, so a user can find or stop it.</param>
public sealed record FinalizeLockHolder(string Command, DateTimeOffset StartedUtc, int ProcessId);

/// <summary>
/// One attempt at the finalise lock: the lock, or null with who holds it. The holder is data, not wording, so a use case can
/// hand it back and leave the refusal to its front end.
/// </summary>
/// <param name="Lock">Held until disposed; null when refused.</param>
/// <param name="Holder">Who refused it; null when taken, or when the holder was still writing its details.</param>
public sealed record FinalizeLockAttempt(IDisposable? Lock, FinalizeLockHolder? Holder);

/// <summary>
/// The database's lock files as every command takes them. They are OS file locks, so they live behind this port. A run
/// holds the run lock for the whole command and the finalise lock while it prepares; a finalisation holds the finalise lock.
/// A refusal hands back the holder, and the front end words it.
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

    /// <summary>
    /// Takes the run lock for a whole run. Null when refused, with <paramref name="holder"/> naming who has it, or null while
    /// the holder is still writing its details.
    /// </summary>
    IDisposable? TakeRun(string databasePath, string command, out LiveRunHolder? holder);

    /// <summary>Whether a finalisation (not a run preparing) holds the finalise lock, read without taking it.</summary>
    bool IsFinaliseGoing(string databasePath);

    /// <summary>
    /// The run going on this database, or null when none is: read from the lock file without taking the lock. A queue verb
    /// holding it for a moment is not a run.
    /// </summary>
    LiveRunHolder? LiveRun(string databasePath);

    /// <summary>
    /// The finalise lock for a command, waiting through a run that is preparing, however long it takes. Refused only while
    /// another finalisation holds it, which the attempt names.
    /// </summary>
    Task<FinalizeLockAttempt> TakeFinalizeAsync(string databasePath, string command, CancellationToken cancellationToken);
}
