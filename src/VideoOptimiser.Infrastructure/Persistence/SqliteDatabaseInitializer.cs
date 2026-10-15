using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Infrastructure.Persistence;

public sealed class SqliteDatabaseInitializer : IDatabaseInitializer
{
    /// <summary>The newest migration below. <see cref="SqliteSchemaCheck"/> refuses a database without it; raise both together.</summary>
    public const int CurrentVersion = 9;

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
        if (!await HasMigrationAsync(connection, 4, cancellationToken))
        {
            await ExecuteAsync(connection, "ALTER TABLE jobs ADD COLUMN execution_mode TEXT NOT NULL DEFAULT 'local';", cancellationToken, transaction);
            await ExecuteAsync(connection, "ALTER TABLE jobs ADD COLUMN remote_host TEXT NULL;", cancellationToken, transaction);
            await ExecuteAsync(connection, "ALTER TABLE jobs ADD COLUMN remote_workspace TEXT NULL;", cancellationToken, transaction);
            await ExecuteAsync(connection, "INSERT INTO schema_migrations(version, applied_utc) VALUES (4, $appliedUtc);", cancellationToken, transaction, new SqliteParameter("$appliedUtc", DateTimeOffset.UtcNow.ToString("O")));
        }
        if (!await HasMigrationAsync(connection, 5, cancellationToken))
        {
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS media_probe_cache (
                    source_path TEXT COLLATE NOCASE PRIMARY KEY,
                    source_size_bytes INTEGER NOT NULL,
                    source_last_write_utc_ticks INTEGER NOT NULL,
                    primary_video_codec TEXT NOT NULL,
                    video_stream_count INTEGER NOT NULL,
                    audio_stream_count INTEGER NOT NULL,
                    subtitle_stream_count INTEGER NOT NULL,
                    attachment_count INTEGER NOT NULL,
                    duration_seconds REAL NULL,
                    size_bytes INTEGER NULL,
                    primary_video_width INTEGER NULL,
                    primary_video_height INTEGER NULL,
                    primary_video_bitrate INTEGER NULL,
                    cache_schema_version INTEGER NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                """, cancellationToken, transaction);
            await ExecuteAsync(connection, "INSERT INTO schema_migrations(version, applied_utc) VALUES (5, $appliedUtc);", cancellationToken, transaction, new SqliteParameter("$appliedUtc", DateTimeOffset.UtcNow.ToString("O")));
        }
        if (!await HasMigrationAsync(connection, 6, cancellationToken))
        {
            // An Immich job is identified by its asset, not by source_path: that holds only the staged copy.
            await ExecuteAsync(connection, """
                ALTER TABLE jobs ADD COLUMN source_kind TEXT NOT NULL DEFAULT 'file';
                ALTER TABLE jobs ADD COLUMN immich_asset_id TEXT NULL;
                ALTER TABLE jobs ADD COLUMN immich_checksum TEXT NULL;
                ALTER TABLE jobs ADD COLUMN immich_new_asset_id TEXT NULL;
                CREATE INDEX IF NOT EXISTS ix_jobs_immich_asset ON jobs(immich_asset_id, status) WHERE immich_asset_id IS NOT NULL;
                CREATE TABLE IF NOT EXISTS immich_skipped_assets (
                    asset_id TEXT PRIMARY KEY,
                    checksum TEXT NOT NULL,
                    rules_fingerprint TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    recorded_utc TEXT NOT NULL
                );
                """, cancellationToken, transaction);
            await ExecuteAsync(connection, "INSERT INTO schema_migrations(version, applied_utc) VALUES (6, $appliedUtc);", cancellationToken, transaction, new SqliteParameter("$appliedUtc", DateTimeOffset.UtcNow.ToString("O")));
        }
        if (!await HasMigrationAsync(connection, 7, cancellationToken))
        {
            // The quality settings that skipped a job as not worth converting; discovery checks the video again once they change.
            await ExecuteAsync(connection, "ALTER TABLE jobs ADD COLUMN quality_fingerprint TEXT NULL;", cancellationToken, transaction);
            await ExecuteAsync(connection, "INSERT INTO schema_migrations(version, applied_utc) VALUES (7, $appliedUtc);", cancellationToken, transaction, new SqliteParameter("$appliedUtc", DateTimeOffset.UtcNow.ToString("O")));
        }
        if (!await HasMigrationAsync(connection, 8, cancellationToken))
        {
            // Video facts on the row itself: the probe cache is keyed by path, and an Immich job's path is a staged copy that
            // cleanup deletes. The backfill copies a job's facts from the cache only while the cache still describes the
            // original: the cached file was last written before the job was created, and a validated job's source size
            // matches. A path whose original was later replaced, as finalisation does with its AV1, holds a newer file.
            // Ticks count 100 ns from 0001-01-01, which is Julian day 1721425.5.
            await ExecuteAsync(connection, """
                ALTER TABLE jobs ADD COLUMN video_codec TEXT NULL;
                ALTER TABLE jobs ADD COLUMN video_width INTEGER NULL;
                ALTER TABLE jobs ADD COLUMN video_height INTEGER NULL;
                ALTER TABLE jobs ADD COLUMN video_bitrate INTEGER NULL;
                ALTER TABLE jobs ADD COLUMN video_duration_seconds REAL NULL;
                ALTER TABLE jobs ADD COLUMN video_size_bytes INTEGER NULL;
                ALTER TABLE immich_skipped_assets ADD COLUMN video_codec TEXT NULL;
                ALTER TABLE immich_skipped_assets ADD COLUMN video_width INTEGER NULL;
                ALTER TABLE immich_skipped_assets ADD COLUMN video_height INTEGER NULL;
                ALTER TABLE immich_skipped_assets ADD COLUMN video_bitrate INTEGER NULL;
                ALTER TABLE immich_skipped_assets ADD COLUMN video_duration_seconds REAL NULL;
                ALTER TABLE immich_skipped_assets ADD COLUMN video_size_bytes INTEGER NULL;
                UPDATE jobs
                SET video_codec = cache.primary_video_codec,
                    video_width = cache.primary_video_width,
                    video_height = cache.primary_video_height,
                    video_bitrate = cache.primary_video_bitrate,
                    video_duration_seconds = cache.duration_seconds,
                    video_size_bytes = cache.source_size_bytes
                FROM media_probe_cache AS cache
                WHERE cache.source_path = jobs.source_path
                  AND cache.source_last_write_utc_ticks / 864000000000.0 + 1721425.5 < julianday(jobs.created_utc)
                  AND (jobs.source_size_bytes IS NULL OR jobs.source_size_bytes = cache.source_size_bytes);
                """, cancellationToken, transaction);
            await ExecuteAsync(connection, "INSERT INTO schema_migrations(version, applied_utc) VALUES (8, $appliedUtc);", cancellationToken, transaction, new SqliteParameter("$appliedUtc", DateTimeOffset.UtcNow.ToString("O")));
        }
        if (!await HasMigrationAsync(connection, 9, cancellationToken))
        {
            // The queue verbs (VOPT-54 step 5): held parks a video, priority is "run next", forced is "queue add --force".
            // row_version makes every job write a compare-and-swap, so a run holding an older copy of a job cannot undo a
            // drop, hold or next made from another terminal while it ran.
            await ExecuteAsync(connection, """
                ALTER TABLE jobs ADD COLUMN held INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE jobs ADD COLUMN priority INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE jobs ADD COLUMN forced INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE jobs ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
                """, cancellationToken, transaction);
            await ExecuteAsync(connection, "INSERT INTO schema_migrations(version, applied_utc) VALUES (9, $appliedUtc);", cancellationToken, transaction, new SqliteParameter("$appliedUtc", DateTimeOffset.UtcNow.ToString("O")));
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
