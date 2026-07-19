using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Diagnostics;

namespace VideoOptimiser.Infrastructure.Diagnostics;

public sealed class SqliteDatabaseInitializer : IDatabaseInitializer
{
    public async Task InitializeAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            // Doctor opens this short-lived connection only to initialise and verify the database.
            // Avoid retaining a Windows file handle after the health check completes.
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_utc TEXT NOT NULL
            );
            """, cancellationToken, transaction);
        await ExecuteAsync(connection, "INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES (1, $appliedUtc);", cancellationToken, transaction, new SqliteParameter("$appliedUtc", DateTimeOffset.UtcNow.ToString("O")));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken, SqliteTransaction? transaction = null, SqliteParameter? parameter = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        if (parameter is not null)
        {
            command.Parameters.Add(parameter);
        }

        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
