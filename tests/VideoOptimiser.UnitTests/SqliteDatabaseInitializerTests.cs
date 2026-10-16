using FluentAssertions;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests;

public sealed class SqliteDatabaseInitializerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public SqliteDatabaseInitializerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task InitializeAsyncKeepsJobsFromBeforeMigrationSixAsFileJobs()
    {
        var databasePath = Path.Combine(_directory, "migration-four.db");
        var jobId = Guid.NewGuid();
        // The jobs table exactly as migrations 2 to 4 left it, with one finished job.
        await ExecuteAsync(databasePath, $$"""
                CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
                INSERT INTO schema_migrations(version, applied_utc) VALUES (1, '2026-01-01T00:00:00.0000000+00:00'), (2, '2026-01-01T00:00:00.0000000+00:00'), (3, '2026-01-01T00:00:00.0000000+00:00'), (4, '2026-01-01T00:00:00.0000000+00:00');
                CREATE TABLE jobs (
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
                    completed_utc TEXT NULL,
                    resume_status INTEGER NULL,
                    attempt INTEGER NOT NULL DEFAULT 0,
                    execution_mode TEXT NOT NULL DEFAULT 'local',
                    remote_host TEXT NULL,
                    remote_workspace TEXT NULL
                );
                INSERT INTO jobs (id, source_path, source_fingerprint, status, crf, created_utc, updated_utc, completed_utc)
                VALUES ('{{jobId:N}}', 'C:\Videos\old.mp4', 'fingerprint', 6, 30, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                """);

        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var job = await repository.GetAsync(databasePath, jobId);

        job!.SourceKind.Should().Be(JobSourceKind.File);
        job.SourcePath.Should().Be(@"C:\Videos\old.mp4");
        job.Crf.Should().Be(30);
        job.ImmichAssetId.Should().BeNull();
        job.ImmichChecksum.Should().BeNull();
        job.ImmichNewAssetId.Should().BeNull();
        (await repository.ListCompletedImmichAsync(databasePath)).Should().BeEmpty();
        (await repository.ListImmichSkipsAsync(databasePath)).Should().BeEmpty();
    }

    [Fact]
    public async Task MigrationEightAddsVideoFactsAndBackfillsJobsWhoseProbeEntryStillDescribesTheOriginal()
    {
        var databasePath = Path.Combine(_directory, "migration-seven.db");
        await RollBackToMigrationSevenAsync(databasePath);
        var queued = Guid.NewGuid();
        var validated = Guid.NewGuid();
        var finalised = Guid.NewGuid();
        var immich = Guid.NewGuid();
        var failed = Guid.NewGuid();
        // The jobs were created on 2026-01-01; a file written after that is not the one they saw.
        var before = new DateTime(2025, 12, 31, 23, 59, 0, DateTimeKind.Utc).Ticks;
        var after = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc).Ticks;
        await ExecuteAsync(databasePath, $$"""
            INSERT INTO jobs (id, source_path, source_fingerprint, status, source_size_bytes, created_utc, updated_utc) VALUES
                ('{{queued:N}}', 'C:\Videos\Queued.mp4', 'f', 0, NULL, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00'),
                ('{{validated:N}}', 'C:\Videos\kept.mp4', 'f', 6, 5000, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00'),
                ('{{finalised:N}}', 'C:\Videos\replaced.mp4', 'f', 6, 9000, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00'),
                ('{{failed:N}}', 'C:\Videos\later.mp4', 'f', 7, NULL, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO jobs (id, source_path, source_fingerprint, status, source_kind, immich_asset_id, created_utc, updated_utc) VALUES
                ('{{immich:N}}', 'C:\Staging\asset\clip.mov', 'f', 12, 'immich', 'asset', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO immich_skipped_assets (asset_id, checksum, rules_fingerprint, reason, recorded_utc) VALUES ('skipped', 'c', 'r', 'no rule for h264', '2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO media_probe_cache (source_path, source_size_bytes, source_last_write_utc_ticks, primary_video_codec, video_stream_count, audio_stream_count, subtitle_stream_count, attachment_count, duration_seconds, size_bytes, primary_video_width, primary_video_height, primary_video_bitrate, cache_schema_version, updated_utc) VALUES
                ('c:\videos\queued.mp4', 7000, {{before}}, 'h264', 1, 1, 0, 0, 30.5, 7000, 1920, 1080, 22000000, 1, '2026-01-01T00:00:00.0000000+00:00'),
                ('C:\Videos\kept.mp4', 5000, {{before}}, 'hevc', 1, 1, 0, 0, 12, 5000, 3840, 2160, NULL, 1, '2026-01-01T00:00:00.0000000+00:00'),
                ('C:\Videos\replaced.mp4', 3000, {{before}}, 'av1', 1, 1, 0, 0, 12, 3000, 1920, 1080, 4000000, 1, '2026-01-01T00:00:00.0000000+00:00'),
                ('C:\Videos\later.mp4', 3000, {{after}}, 'av1', 1, 1, 0, 0, 12, 3000, 1920, 1080, 4000000, 1, '2026-01-01T00:00:00.0000000+00:00');
            """);

        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());

        (await repository.GetAsync(databasePath, queued))!.VideoFacts.Should().Be(new VideoFacts("h264", 1920, 1080, 22_000_000, 30.5, 7000));
        (await repository.GetAsync(databasePath, validated))!.VideoFacts.Should().Be(new VideoFacts("hevc", 3840, 2160, null, 12, 5000));
        // The cache now describes a file that replaced the original: smaller than the one validated, or written after the job began.
        (await repository.GetAsync(databasePath, finalised))!.VideoFacts.Should().BeNull();
        (await repository.GetAsync(databasePath, failed))!.VideoFacts.Should().BeNull();
        (await repository.GetAsync(databasePath, immich))!.VideoFacts.Should().BeNull();
        (await repository.ListImmichSkipsAsync(databasePath))["skipped"].VideoFacts.Should().BeNull();
    }

    [Fact]
    public async Task MigrationTenAddsTheSingleRowRunRequestsTable()
    {
        var databasePath = Path.Combine(_directory, "migration-nine.db");
        await new SqliteDatabaseInitializer().InitializeAsync(databasePath);
        // A database exactly as migration 9 left it.
        await ExecuteAsync(databasePath, "DROP TABLE run_requests; DELETE FROM schema_migrations WHERE version = 10;");

        await new SqliteDatabaseInitializer().InitializeAsync(databasePath);

        await ExecuteAsync(databasePath, "INSERT INTO run_requests(id, updated_utc) VALUES (1, 'now');");
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT stop_after_video, stop_now, worker_fate, (SELECT COUNT(*) FROM schema_migrations WHERE version = 10) FROM run_requests;";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        (reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2), reader.GetInt32(3)).Should().Be((0, 0, true, 1));
        SqliteDatabaseInitializer.CurrentVersion.Should().Be(10);

        // The id check keeps it one row.
        var second = () => ExecuteAsync(databasePath, "INSERT INTO run_requests(id, updated_utc) VALUES (2, 'now');");
        await second.Should().ThrowAsync<SqliteException>();
    }

    /// <summary>A database exactly as migration 7 left it: the current schema without migration 8's columns.</summary>
    private static async Task RollBackToMigrationSevenAsync(string databasePath)
    {
        await new SqliteDatabaseInitializer().InitializeAsync(databasePath);
        var columns = new[] { "video_codec", "video_width", "video_height", "video_bitrate", "video_duration_seconds", "video_size_bytes" };
        await ExecuteAsync(databasePath, string.Join(Environment.NewLine,
            columns.SelectMany(column => new[] { $"ALTER TABLE jobs DROP COLUMN {column};", $"ALTER TABLE immich_skipped_assets DROP COLUMN {column};" })
                .Append("DELETE FROM schema_migrations WHERE version = 8;")));
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
