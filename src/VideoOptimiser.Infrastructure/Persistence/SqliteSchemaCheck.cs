using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Infrastructure.Persistence;

/// <summary>
/// The initializer for readers that must leave the database as they found it: it migrates nothing and throws
/// <see cref="DatabaseOutdatedException"/> unless every migration is already applied, because reading an older schema would
/// fail on its missing columns.
/// </summary>
public sealed class SqliteSchemaCheck : IDatabaseInitializer
{
    public async Task InitializeAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        object? version;
        try
        {
            version = await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (SqliteException)
        {
            // No schema_migrations table: a database no version of the app has initialised.
            throw new DatabaseOutdatedException(databasePath);
        }

        if (version is null or DBNull || Convert.ToInt64(version, CultureInfo.InvariantCulture) < SqliteDatabaseInitializer.CurrentVersion)
            throw new DatabaseOutdatedException(databasePath);
    }
}
