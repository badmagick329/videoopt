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
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                source_path TEXT NOT NULL,
                source_fingerprint TEXT NOT NULL,
                status INTEGER NOT NULL,
                crf INTEGER NULL,
                output_path TEXT NULL,
                manifest_path TEXT NULL,
                validation_passed INTEGER NULL,
                source_size_bytes INTEGER NULL,
                output_size_bytes INTEGER NULL,
                percentage_saved TEXT NULL,
                failure_category TEXT NULL,
                failure_message TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                completed_utc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_jobs_source_active ON jobs(source_path, source_fingerprint, status);
            CREATE INDEX IF NOT EXISTS ix_jobs_status_updated ON jobs(status, updated_utc DESC);
            """, cancellationToken, transaction);
        await ExecuteAsync(connection, "INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES (2, $appliedUtc);", cancellationToken, transaction, new SqliteParameter("$appliedUtc", DateTimeOffset.UtcNow.ToString("O")));
        if (!await HasMigrationAsync(connection, 3, cancellationToken))
        {
            await ExecuteAsync(connection, "ALTER TABLE jobs ADD COLUMN resume_status INTEGER NULL;", cancellationToken, transaction);
            await ExecuteAsync(connection, "ALTER TABLE jobs ADD COLUMN attempt INTEGER NOT NULL DEFAULT 0;", cancellationToken, transaction);
            await ExecuteAsync(connection, "INSERT INTO schema_migrations(version, applied_utc) VALUES (3, $appliedUtc);", cancellationToken, transaction, new SqliteParameter("$appliedUtc", DateTimeOffset.UtcNow.ToString("O")));
        }
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

    private static async Task<bool> HasMigrationAsync(SqliteConnection connection, int version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version = $version);";
        command.Parameters.AddWithValue("$version", version);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }
}
