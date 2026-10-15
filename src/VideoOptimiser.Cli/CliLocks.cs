using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Infrastructure.Persistence;
using static CliOutput;

/// <summary>
/// The database locks the commands take (VOPT-54 step 3). A run holds the run lock for the whole command, so a second run
/// cannot mark its active jobs interrupted; a finalisation holds the finalise lock, so a run leaves its job alone. Finalize
/// does not take the run lock: reviewing and finalising during an all-day run is the point of a separate finalise.
/// </summary>
internal static class CliLocks
{
    /// <summary>The run lock, or null after saying who holds it. Taking it starts the run: from then on every end decides the worker's fate (<see cref="CliApplication.RunGuardedAsync"/>).</summary>
    /// <param name="command">How the refusal names this run to a later one, such as <c>video-optimiser run</c>.</param>
    internal static DatabaseLock? TakeRunLock(IServiceProvider services, LoadedConfiguration configuration, string command)
    {
        var taken = DatabaseLock.TryAcquire(DatabaseLock.RunPath(configuration.Settings.Database.Path), command, out var holder);
        if (taken is not null)
        {
            services.GetRequiredService<RunWorkerState>().Started = true;
            return taken;
        }
        if (holder is null)
        {
            WriteError("Another run is starting on this database. Try again in a few seconds.");
            return null;
        }
        WriteError($"Another run is using this database: {holder.Command}, started {FormatStarted(holder.StartedUtc)} (process {holder.ProcessId}).");
        WriteError("Wait for it to finish, or stop it with Ctrl+C in its window.");
        return null;
    }

    /// <summary>How a run names itself in the run lock, and so to a command refused by it.</summary>
    internal const string RunHolder = "video-optimiser run";
    internal const string ImmichRunHolder = "video-optimiser immich run";

    /// <summary>The queue verbs take the run lock for a moment to see whether a run holds it (<see cref="CliApplication.RunHoldsJobsAsync"/>).</summary>
    internal static readonly HashSet<string> QueueVerbHolders = new(StringComparer.Ordinal) { "queue hold", "queue next", "queue drop" };

    /// <summary>
    /// How the run going on this database names itself, or null when none is: read from the lock file without taking the
    /// lock (W5). A queue verb holding it for a moment is not a run.
    /// </summary>
    internal static string? LiveRun(string databasePath) =>
        DatabaseLock.ReadLiveHolder(DatabaseLock.RunPath(databasePath)) is { } holder && !QueueVerbHolders.Contains(holder.Command) ? holder.Command : null;

    /// <summary>
    /// The finalise lock, or null after saying who holds it. A run holds it while it prepares, which takes moments, so this
    /// waits for a run however long it takes and refuses only another finalise.
    /// </summary>
    /// <param name="refusal">What to say instead when a finalise holds it, for a command other than finalize.</param>
    internal static async Task<DatabaseLock?> TakeFinalizeLockAsync(LoadedConfiguration configuration, string command, CancellationToken cancellationToken, string? refusal = null)
    {
        DatabaseLock? taken;
        DatabaseLock.Holder? holder;
        do
        {
            (taken, holder) = await DatabaseLock.AcquireAsync(DatabaseLock.FinalizePath(configuration.Settings.Database.Path), command, TimeSpan.FromSeconds(5), cancellationToken);
        }
        while (taken is null && holder?.Command == DatabaseLock.RunPreparing);
        if (taken is not null) return taken;
        WriteError(refusal ?? (holder is null
            ? "Another finalise is running on this database. Wait for it to finish."
            : $"Another finalise is running on this database, started {FormatStarted(holder.StartedUtc)} (process {holder.ProcessId}). Wait for it to finish."));
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
