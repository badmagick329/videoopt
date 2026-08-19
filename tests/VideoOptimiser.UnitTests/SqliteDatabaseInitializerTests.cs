using FluentAssertions;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Infrastructure.Diagnostics;

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
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 5;";
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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
