using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Infrastructure.Persistence;

/// <summary>
/// How every store opens its connection. The store's own connection checks the schema first, so a current database costs one
/// connection and one indexed read; the initializer, which opens a connection and a transaction of its own, runs only when
/// that check finds the schema missing or old.
/// </summary>
/// <remarks>
/// The check runs on every open instead of being remembered per path. Several processes share one database, and the file can
/// be deleted or replaced under a live process (tests do, and so can a user), so a remembered "initialised" would skip creating
/// the schema in a new file. The test counts the applied versions 1 to <see cref="SqliteDatabaseInitializer.CurrentVersion"/> in schema_migrations
/// (a primary-key range read), not the highest one: the initializer decides per version, so a missing middle row must still
/// send us to it. Migrations commit in one transaction, so a reader sees the old state or the current one. Any open or read
/// failure falls back to the initializer, which creates the file or surfaces the real error.
/// </remarks>
internal static class SqliteStoreConnection
{
    public static async Task<SqliteConnection> OpenAsync(IDatabaseInitializer databaseInitializer, string databasePath, SqliteConnectionStringBuilder builder, CancellationToken cancellationToken)
    {
        builder.DataSource = Path.GetFullPath(databasePath);
        var connectionString = builder.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            if (await IsCurrentAsync(connection, cancellationToken)) return connection;
        }
        catch (SqliteException)
        {
        }
        catch
        {
            // Cancellation above all: a run's poll is cancelled mid-read when the run ends. Without pooling, an undisposed
            // connection holds the file open until the finalizer runs, so the database's folder could not be deleted or moved.
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        await databaseInitializer.InitializeAsync(databasePath, cancellationToken);
        connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<bool> IsCurrentAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$current", SqliteDatabaseInitializer.CurrentVersion);
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version BETWEEN 1 AND $current;";
        try
        {
            var version = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(version, CultureInfo.InvariantCulture) == SqliteDatabaseInitializer.CurrentVersion;
        }
        catch (SqliteException)
        {
            // No schema_migrations table: a file nothing has initialised yet.
            return false;
        }
    }
}
