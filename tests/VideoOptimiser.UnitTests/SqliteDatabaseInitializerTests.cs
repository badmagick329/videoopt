using FluentAssertions;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Jobs;

namespace VideoOptimiser.UnitTests;

public sealed class SqliteDatabaseInitializerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public SqliteDatabaseInitializerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task InitializeAsyncIsIdempotentAndRecordsJobMigration()
    {
        var databasePath = Path.Combine(_directory, "jobs.db");
        var initializer = new SqliteDatabaseInitializer();

        await initializer.InitializeAsync(databasePath);
        await initializer.InitializeAsync(databasePath);

        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 6;";
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        count.Should().Be(1);

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'jobs';";
        var jobsTableCount = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        jobsTableCount.Should().Be(1);

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'media_probe_cache';";
        var cacheTableCount = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        cacheTableCount.Should().Be(1);
    }

    [Fact]
    public async Task InitializeAsyncAddsProbeCacheToAnExistingMigrationFourDatabase()
    {
        var databasePath = Path.Combine(_directory, "migration-four.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
                CREATE TABLE jobs (
                    id TEXT PRIMARY KEY,
                    source_path TEXT NOT NULL,
                    source_fingerprint TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                INSERT INTO schema_migrations(version, applied_utc) VALUES (1, '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO schema_migrations(version, applied_utc) VALUES (2, '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO schema_migrations(version, applied_utc) VALUES (3, '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO schema_migrations(version, applied_utc) VALUES (4, '2026-01-01T00:00:00.0000000+00:00');
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        await new SqliteDatabaseInitializer().InitializeAsync(databasePath);

        await using var verified = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await verified.OpenAsync();
        await using var verifiedCommand = verified.CreateCommand();
        verifiedCommand.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 5;";
        Convert.ToInt64(await verifiedCommand.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1);
        verifiedCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'media_probe_cache';";
        Convert.ToInt64(await verifiedCommand.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1);
    }

    [Fact]
    public async Task InitializeAsyncKeepsMigrationFiveJobsAsFileJobs()
    {
        var databasePath = Path.Combine(_directory, "migration-five.db");
        var jobId = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // The jobs table exactly as migrations 2 to 4 left it, with one finished job.
            command.CommandText = $$"""
                CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
                INSERT INTO schema_migrations(version, applied_utc) VALUES (1, '2026-01-01T00:00:00.0000000+00:00'), (2, '2026-01-01T00:00:00.0000000+00:00'), (3, '2026-01-01T00:00:00.0000000+00:00'), (4, '2026-01-01T00:00:00.0000000+00:00'), (5, '2026-01-01T00:00:00.0000000+00:00');
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
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
