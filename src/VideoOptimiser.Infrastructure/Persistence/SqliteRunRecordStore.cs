using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Infrastructure.Persistence;

/// <summary>The <c>runs</c> and <c>conversion_spans</c> tables (migration 13). Each call is one statement.</summary>
public sealed class SqliteRunRecordStore(IDatabaseInitializer databaseInitializer) : IRunRecordStore
{
    public Task AddRunAsync(string databasePath, Guid runId, string holderName, DateTimeOffset startedUtc, CancellationToken cancellationToken) =>
        ExecuteAsync(databasePath, "INSERT INTO runs (id, started_utc, holder_name) VALUES ($id, $startedUtc, $holderName);", cancellationToken,
            ("$id", runId.ToString("N")), ("$startedUtc", Time(startedUtc)), ("$holderName", holderName));

    public Task EndRunAsync(string databasePath, Guid runId, DateTimeOffset endedUtc, CancellationToken cancellationToken) =>
        ExecuteAsync(databasePath, "UPDATE runs SET ended_utc = $endedUtc WHERE id = $id;", cancellationToken,
            ("$id", runId.ToString("N")), ("$endedUtc", Time(endedUtc)));

    public Task SetRunWorkerAsync(string databasePath, Guid runId, long workerServerId, CancellationToken cancellationToken) =>
        ExecuteAsync(databasePath, "UPDATE runs SET worker_server_id = $serverId WHERE id = $id;", cancellationToken,
            ("$id", runId.ToString("N")), ("$serverId", workerServerId));

    public async Task<long> OpenSpanAsync(string databasePath, Guid runId, Guid jobId, long? workerServerId, DateTimeOffset startedUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversion_spans (job_id, run_id, worker_server_id, started_utc) VALUES ($jobId, $runId, $serverId, $startedUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("N"));
        command.Parameters.AddWithValue("$runId", runId.ToString("N"));
        command.Parameters.AddWithValue("$serverId", (object?)workerServerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$startedUtc", Time(startedUtc));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public Task CloseSpanAsync(string databasePath, long spanId, DateTimeOffset endedUtc, CancellationToken cancellationToken) =>
        ExecuteAsync(databasePath, "UPDATE conversion_spans SET ended_utc = $endedUtc WHERE id = $id;", cancellationToken,
            ("$id", spanId), ("$endedUtc", Time(endedUtc)));

    internal static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private async Task ExecuteAsync(string databasePath, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(string databasePath, CancellationToken cancellationToken)
    {
        await databaseInitializer.InitializeAsync(databasePath, cancellationToken);
        var builder = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
