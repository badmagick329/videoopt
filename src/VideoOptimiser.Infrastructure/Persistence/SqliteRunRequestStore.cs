using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Infrastructure.Persistence;

/// <summary>
/// The <c>run_requests</c> row (migration 10). Every call is one statement, so a stop requested from another process lands
/// whole even while the run resets or sets the fate. Upserts, because the row exists only after the first run or request.
/// </summary>
public sealed class SqliteRunRequestStore(IDatabaseInitializer databaseInitializer) : IRunRequestStore
{
    private const string DeleteFate = "delete";
    private const string KeepFate = "keep";

    public Task ResetAsync(string databasePath, WorkerFateChoice? fate, CancellationToken cancellationToken) =>
        UpsertAsync(databasePath, "stop_after_video = 0, stop_now = 0, worker_fate = $fate", 0, 0, FateText(fate), cancellationToken);

    public async Task<RunRequests> ReadAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT stop_after_video, stop_now, worker_fate FROM run_requests WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new RunRequests(false, false, null);
        return new RunRequests(reader.GetInt32(0) != 0, reader.GetInt32(1) != 0, ParseFate(reader.IsDBNull(2) ? null : reader.GetString(2)));
    }

    public Task RequestStopAsync(string databasePath, bool now, CancellationToken cancellationToken) =>
        now
            ? UpsertAsync(databasePath, "stop_now = 1", 0, 1, null, cancellationToken)
            : UpsertAsync(databasePath, "stop_after_video = 1", 1, 0, null, cancellationToken);

    public Task SetWorkerFateAsync(string databasePath, WorkerFateChoice fate, CancellationToken cancellationToken) =>
        UpsertAsync(databasePath, "worker_fate = $fate", 0, 0, FateText(fate), cancellationToken);

    /// <param name="assignments">What the row changes to when it exists; the insert carries the same values for a new row, the other columns at their defaults.</param>
    private async Task UpsertAsync(string databasePath, string assignments, int stopAfterVideo, int stopNow, string? fate, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO run_requests (id, stop_after_video, stop_now, worker_fate, updated_utc)
            VALUES (1, $stopAfterVideo, $stopNow, $fate, $updatedUtc)
            ON CONFLICT(id) DO UPDATE SET {assignments}, updated_utc = $updatedUtc;
            """;
        command.Parameters.AddWithValue("$stopAfterVideo", stopAfterVideo);
        command.Parameters.AddWithValue("$stopNow", stopNow);
        command.Parameters.AddWithValue("$fate", (object?)fate ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? FateText(WorkerFateChoice? fate) => fate switch
    {
        WorkerFateChoice.Delete => DeleteFate,
        WorkerFateChoice.Keep => KeepFate,
        _ => null
    };

    /// <summary>Anything but the two known words reads as no choice, so a row from a newer version cannot pick a fate by accident.</summary>
    private static WorkerFateChoice? ParseFate(string? text) => text switch
    {
        DeleteFate => WorkerFateChoice.Delete,
        KeepFate => WorkerFateChoice.Keep,
        _ => null
    };

    private Task<SqliteConnection> OpenAsync(string databasePath, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder { Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true, Pooling = false };
        return SqliteStoreConnection.OpenAsync(databaseInitializer, databasePath, builder, cancellationToken);
    }
}
