using Microsoft.Data.Sqlite;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// Reads the run records (migration 13) straight from a test database. This subtask only writes them; their reading port
/// comes with the cost report, so the tests read the tables as they are stored.
/// </summary>
internal static class RecordRows
{
    /// <summary>Every row of <paramref name="sql"/>, each column as SQLite returns it, <see cref="DBNull"/> read as null.</summary>
    public static async Task<List<object?[]>> QueryAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var column = 0; column < row.Length; column++) row[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            rows.Add(row);
        }
        return rows;
    }

    public static Task ExecuteAsync(string databasePath, string sql) => QueryAsync(databasePath, sql);
}
