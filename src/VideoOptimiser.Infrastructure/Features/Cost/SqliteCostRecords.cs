using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Features.Cost;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.Infrastructure.Features.Cost;

/// <summary>
/// Reads the run records (migration 13) and the jobs they name, on a read-only connection. A database an older version left
/// is migrated first, as every other command's first read does, so the tables exist; a database that does not exist yet is
/// not created, as there is nothing to cost.
/// </summary>
public sealed class SqliteCostRecords(IDatabaseInitializer databaseInitializer) : ICostRecords
{
    public async Task<CostRecords> ReadAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath)) return CostRecords.None;
        var builder = new SqliteConnectionStringBuilder { Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = await SqliteStoreConnection.OpenAsync(databaseInitializer, databasePath, builder, cancellationToken);

        var workers = await ReadAsync(connection, "SELECT server_id, name, hourly_gross, created_utc, deleted_utc, end_estimated FROM workers;", reader => new CostWorkerRow(
            reader.GetInt64(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : decimal.Parse(reader.GetString(2), NumberStyles.Number, CultureInfo.InvariantCulture),
            Time(reader.GetString(3)), reader.IsDBNull(4) ? null : Time(reader.GetString(4)), reader.GetInt64(5) != 0), cancellationToken);
        var runs = await ReadAsync(connection, "SELECT id, started_utc, ended_utc, worker_server_id FROM runs;", reader => new CostRunRow(
            Guid.ParseExact(reader.GetString(0), "N"), Time(reader.GetString(1)), reader.IsDBNull(2) ? null : Time(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetInt64(3)), cancellationToken);
        var spans = await ReadAsync(connection, "SELECT job_id, run_id, worker_server_id, started_utc, ended_utc FROM conversion_spans WHERE worker_server_id IS NOT NULL;", reader => new CostSpanRow(
            Guid.ParseExact(reader.GetString(0), "N"), Guid.ParseExact(reader.GetString(1), "N"), reader.GetInt64(2), Time(reader.GetString(3)), reader.IsDBNull(4) ? null : Time(reader.GetString(4))), cancellationToken);
        var jobs = await ReadAsync(connection, """
            SELECT id, source_path, status, source_size_bytes, output_size_bytes FROM jobs
            WHERE id IN (SELECT job_id FROM conversion_spans WHERE worker_server_id IS NOT NULL);
            """, reader => new CostJobRow(
            Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), (JobStatus)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4)), cancellationToken);
        return new CostRecords(workers, runs, spans, jobs);
    }

    private static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static async Task<List<T>> ReadAsync<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> row, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(row(reader));
        return rows;
    }
}
