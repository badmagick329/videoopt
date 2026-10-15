using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Infrastructure.Persistence;
using static CliOutput;

/// <summary>
/// The database locks the commands take (VOPT-54 step 3). A run holds the run lock for the whole command, so a second run
/// cannot mark its active jobs interrupted (<c>RunWorkerEnd.TakeRunLock</c>, since taking it starts the run); a finalisation
/// holds the finalise lock, so a run leaves its job alone. Finalize does not take the run lock: reviewing and finalising
/// during an all-day run is the point of a separate finalise.
/// </summary>
internal static class CliLocks
{
    /// <summary>
    /// The finalise lock, or null after saying who holds it. A run holds it while it prepares, which takes moments, so this
    /// waits for a run however long it takes and refuses only another finalise.
    /// </summary>
    internal static async Task<DatabaseLock?> TakeFinalizeLockAsync(LoadedConfiguration configuration, string command, CancellationToken cancellationToken)
    {
        var (taken, holder) = await DatabaseLock.AcquireFinalizeAsync(configuration.Settings.Database.Path, command, cancellationToken);
        if (taken is not null) return taken;
        WriteError(holder is null
            ? "Another finalise is running on this database. Wait for it to finish."
            : $"Another finalise is running on this database, started {FormatStarted(holder.StartedUtc)} (process {holder.ProcessId}). Wait for it to finish.");
        return null;
    }

    /// <summary>Local time; the date only when it was not today, as a run can be left going overnight.</summary>
    internal static string FormatStarted(DateTimeOffset startedUtc)
    {
        var local = startedUtc.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
