using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Infrastructure.Persistence;

/// <summary>A managed worker as its bill counts it: Hetzner bills from creation to deletion, per started hour.</summary>
/// <param name="HourlyGross">The server type's hourly gross in its location plus the Primary IPv4's there; null when Hetzner listed no price for the type.</param>
public sealed record WorkerBillEntry(long ServerId, string Name, string ServerType, string Location, decimal? HourlyGross, DateTimeOffset CreatedUtc);

/// <summary>
/// The <c>workers</c> table (migration 13): one row per managed worker that ever existed, written only by the Hetzner worker
/// lifecycle, which is the only code that sees a worker created, seen or deleted. Each call is one statement. Hetzner never
/// reuses a server id, so a row once ended stays ended.
/// </summary>
public sealed class SqliteWorkerBillStore(IDatabaseInitializer databaseInitializer)
{
    /// <summary>Adds the worker, seen up at <paramref name="seenUtc"/>; a row already there is left as it is.</summary>
    public Task AddAsync(string databasePath, WorkerBillEntry entry, DateTimeOffset seenUtc, CancellationToken cancellationToken) =>
        ExecuteAsync(databasePath, """
            INSERT INTO workers (server_id, name, server_type, location, hourly_gross, created_utc, last_seen_utc)
            VALUES ($serverId, $name, $serverType, $location, $hourlyGross, $createdUtc, $seenUtc)
            ON CONFLICT(server_id) DO NOTHING;
            """, cancellationToken,
            ("$serverId", entry.ServerId), ("$name", entry.Name), ("$serverType", entry.ServerType), ("$location", entry.Location),
            ("$hourlyGross", entry.HourlyGross?.ToString(CultureInfo.InvariantCulture)), ("$createdUtc", SqliteRunRecordStore.Time(entry.CreatedUtc)), ("$seenUtc", SqliteRunRecordStore.Time(seenUtc)));

    /// <returns>False when no row of a worker still up names it, so the caller adds one.</returns>
    public async Task<bool> SeenAsync(string databasePath, long serverId, DateTimeOffset seenUtc, CancellationToken cancellationToken) =>
        await ExecuteAsync(databasePath, "UPDATE workers SET last_seen_utc = $seenUtc WHERE server_id = $serverId AND deleted_utc IS NULL;", cancellationToken,
            ("$serverId", serverId), ("$seenUtc", SqliteRunRecordStore.Time(seenUtc))) > 0;

    /// <summary>Hetzner confirmed the deletion at <paramref name="deletedUtc"/>.</summary>
    public Task DeletedAsync(string databasePath, long serverId, DateTimeOffset deletedUtc, CancellationToken cancellationToken) =>
        ExecuteAsync(databasePath, "UPDATE workers SET deleted_utc = $deletedUtc, last_seen_utc = $deletedUtc WHERE server_id = $serverId AND deleted_utc IS NULL;", cancellationToken,
            ("$serverId", serverId), ("$deletedUtc", SqliteRunRecordStore.Time(deletedUtc)));

    /// <summary>
    /// The worker was found gone, deleted outside VideoOptimiser at some time nobody recorded: the last time it was seen up is
    /// the closest end known, and the row says it is an estimate.
    /// </summary>
    public Task GoneAsync(string databasePath, long serverId, CancellationToken cancellationToken) =>
        ExecuteAsync(databasePath, "UPDATE workers SET deleted_utc = last_seen_utc, end_estimated = 1 WHERE server_id = $serverId AND deleted_utc IS NULL;", cancellationToken,
            ("$serverId", serverId));

    private async Task<int> ExecuteAsync(string databasePath, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await databaseInitializer.InitializeAsync(databasePath, cancellationToken);
        var builder = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
