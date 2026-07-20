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
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 2;";
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        count.Should().Be(1);

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'jobs';";
        var jobsTableCount = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        jobsTableCount.Should().Be(1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
