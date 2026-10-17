namespace VideoOptimiser.Application.Jobs;

/// <summary>
/// The permanent record of runs and conversion spans (migration 13) that the cost of the freed space is worked out from
/// (VOPT-126). Rows are only added and closed, never deleted. A run killed before its end keeps no end time, and neither does
/// a span it had open; a reader takes the run's last span end instead. The managed workers' rows are written by the worker
/// lifecycle itself, in Infrastructure.
/// </summary>
public interface IRunRecordStore
{
    /// <summary>Called once the run holds the run lock.</summary>
    /// <param name="holderName">How the run names itself in the lock, such as <see cref="LockHolders.BackgroundRun"/>.</param>
    Task AddRunAsync(string databasePath, Guid runId, string holderName, DateTimeOffset startedUtc, CancellationToken cancellationToken);

    Task EndRunAsync(string databasePath, Guid runId, DateTimeOffset endedUtc, CancellationToken cancellationToken);

    /// <summary>The managed worker the run used; never called for this PC or a manual SSH host.</summary>
    Task SetRunWorkerAsync(string databasePath, Guid runId, long workerServerId, CancellationToken cancellationToken);

    /// <param name="workerServerId">The run's managed worker when the span opens; null on this PC, on a manual host, and before the worker was ensured.</param>
    /// <returns>The span's id, for <see cref="CloseSpanAsync"/>.</returns>
    Task<long> OpenSpanAsync(string databasePath, Guid runId, Guid jobId, long? workerServerId, DateTimeOffset startedUtc, CancellationToken cancellationToken);

    Task CloseSpanAsync(string databasePath, long spanId, DateTimeOffset endedUtc, CancellationToken cancellationToken);
}
