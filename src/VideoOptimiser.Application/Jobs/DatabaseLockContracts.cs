namespace VideoOptimiser.Application.Jobs;

/// <summary>
/// The database's lock files as use cases take them. They are OS file locks, so they live behind this port; so far it has
/// only the finalise lock a run holds while it prepares, and the CLI still takes the others through <c>DatabaseLock</c>.
/// </summary>
public interface IDatabaseLocks
{
    /// <summary>
    /// Takes the finalise lock as a run preparing, which a finalisation that starts meanwhile waits on instead of being
    /// refused. Null while a finalisation holds it: that finalisation's jobs are live.
    /// </summary>
    IDisposable? TryHoldFinalizeWhilePreparing(string databasePath);
}
