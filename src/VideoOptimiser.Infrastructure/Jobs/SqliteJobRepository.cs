using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Infrastructure.Jobs;

public sealed class SqliteJobRepository(IDatabaseInitializer databaseInitializer) : IJobRepository
{
    public async Task<JobRecord?> FindActiveAsync(string databasePath, string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM jobs
            WHERE source_path = $sourcePath COLLATE NOCASE
              AND source_fingerprint = $sourceFingerprint
              AND status NOT IN ($completed, $failed, $cancelled)
            ORDER BY created_utc DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sourcePath", sourcePath);
        command.Parameters.AddWithValue("$sourceFingerprint", sourceFingerprint);
        command.Parameters.AddWithValue("$completed", (int)JobStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)JobStatus.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)JobStatus.Cancelled);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<JobRecord?> FindOpenBySourceAsync(string databasePath, string sourcePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jobs WHERE source_path = $sourcePath COLLATE NOCASE AND status NOT IN ($completed, $failed, $cancelled) ORDER BY created_utc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$sourcePath", sourcePath);
        command.Parameters.AddWithValue("$completed", (int)JobStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)JobStatus.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)JobStatus.Cancelled);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<JobRecord?> GetAsync(string databasePath, Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<JobRecord> CreateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default)
    {
        job.CreatedUtc = DateTimeOffset.UtcNow;
        job.UpdatedUtc = job.CreatedUtc;
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await ExecuteWriteAsync(connection, job, insert: true, cancellationToken);
        return job;
    }

    public async Task UpdateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default)
    {
        job.UpdatedUtc = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        var current = await GetStatusAsync(connection, job.Id, cancellationToken);
        if (current is null) throw new InvalidOperationException($"Job {job.Id:N} does not exist.");
        if (!JobStateTransitions.IsAllowed(current.Value, job.Status)) throw new InvalidOperationException($"Cannot change job {job.Id:N} from {current} to {job.Status}.");
        await ExecuteWriteAsync(connection, job, insert: false, cancellationToken);
    }

    public async Task<IReadOnlyList<JobRecord>> ListAsync(string databasePath, bool terminal, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = terminal
            ? "SELECT * FROM jobs WHERE status IN ($completed, $failed, $cancelled) ORDER BY updated_utc DESC;"
            : "SELECT * FROM jobs WHERE status NOT IN ($completed, $failed, $cancelled) ORDER BY updated_utc DESC;";
        command.Parameters.AddWithValue("$completed", (int)JobStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)JobStatus.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)JobStatus.Cancelled);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jobs = new List<JobRecord>();
        while (await reader.ReadAsync(cancellationToken)) jobs.Add(Read(reader));
        return jobs;
    }

    public async Task MarkActiveJobsInterruptedAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET resume_status = status, status = $interrupted, failure_category = 'Interrupted', failure_message = 'Application stopped before the job completed.', updated_utc = $updatedUtc
            WHERE status IN ($crfSearching, $encoding, $validating, $finalizing);
            """;
        command.Parameters.AddWithValue("$interrupted", (int)JobStatus.Interrupted);
        command.Parameters.AddWithValue("$crfSearching", (int)JobStatus.CrfSearching);
        command.Parameters.AddWithValue("$encoding", (int)JobStatus.Encoding);
        command.Parameters.AddWithValue("$validating", (int)JobStatus.Validating);
        command.Parameters.AddWithValue("$finalizing", (int)JobStatus.Finalizing);
        command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(string databasePath, CancellationToken cancellationToken)
    {
        await databaseInitializer.InitializeAsync(databasePath, cancellationToken);
        var builder = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteWriteAsync(SqliteConnection connection, JobRecord job, bool insert, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = insert
            ? """INSERT INTO jobs (id, source_path, source_fingerprint, status, resume_status, attempt, crf, output_path, manifest_path, validation_passed, source_size_bytes, output_size_bytes, percentage_saved, failure_category, failure_message, created_utc, updated_utc, completed_utc) VALUES ($id, $sourcePath, $sourceFingerprint, $status, $resumeStatus, $attempt, $crf, $outputPath, $manifestPath, $validationPassed, $sourceSizeBytes, $outputSizeBytes, $percentageSaved, $failureCategory, $failureMessage, $createdUtc, $updatedUtc, $completedUtc);"""
            : """UPDATE jobs SET source_path = $sourcePath, source_fingerprint = $sourceFingerprint, status = $status, resume_status = $resumeStatus, attempt = $attempt, crf = $crf, output_path = $outputPath, manifest_path = $manifestPath, validation_passed = $validationPassed, source_size_bytes = $sourceSizeBytes, output_size_bytes = $outputSizeBytes, percentage_saved = $percentageSaved, failure_category = $failureCategory, failure_message = $failureMessage, updated_utc = $updatedUtc, completed_utc = $completedUtc WHERE id = $id;""";
        Add(command, "$id", job.Id.ToString("N"));
        Add(command, "$sourcePath", job.SourcePath);
        Add(command, "$sourceFingerprint", job.SourceFingerprint);
        Add(command, "$status", (int)job.Status);
        Add(command, "$resumeStatus", job.ResumeStatus is null ? null : (int)job.ResumeStatus.Value);
        Add(command, "$attempt", job.Attempt);
        Add(command, "$crf", job.Crf);
        Add(command, "$outputPath", job.OutputPath);
        Add(command, "$manifestPath", job.ManifestPath);
        Add(command, "$validationPassed", job.ValidationPassed is null ? null : job.ValidationPassed.Value ? 1 : 0);
        Add(command, "$sourceSizeBytes", job.SourceSizeBytes);
        Add(command, "$outputSizeBytes", job.OutputSizeBytes);
        Add(command, "$percentageSaved", job.PercentageSaved?.ToString(CultureInfo.InvariantCulture));
        Add(command, "$failureCategory", job.FailureCategory);
        Add(command, "$failureMessage", job.FailureMessage);
        Add(command, "$createdUtc", job.CreatedUtc.ToString("O"));
        Add(command, "$updatedUtc", job.UpdatedUtc.ToString("O"));
        Add(command, "$completedUtc", job.CompletedUtc?.ToString("O"));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<JobStatus?> GetStatusAsync(SqliteConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : (JobStatus)Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static JobRecord Read(SqliteDataReader reader) => new()
    {
        Id = Guid.ParseExact(reader.GetString(reader.GetOrdinal("id")), "N"),
        SourcePath = reader.GetString(reader.GetOrdinal("source_path")),
        SourceFingerprint = reader.GetString(reader.GetOrdinal("source_fingerprint")),
        Status = (JobStatus)reader.GetInt32(reader.GetOrdinal("status")),
        ResumeStatus = NullableInt(reader, "resume_status") is { } resumeStatus ? (JobStatus)resumeStatus : null,
        Attempt = reader.GetInt32(reader.GetOrdinal("attempt")),
        Crf = NullableInt(reader, "crf"),
        OutputPath = NullableString(reader, "output_path"),
        ManifestPath = NullableString(reader, "manifest_path"),
        ValidationPassed = NullableBool(reader, "validation_passed"),
        SourceSizeBytes = NullableLong(reader, "source_size_bytes"),
        OutputSizeBytes = NullableLong(reader, "output_size_bytes"),
        PercentageSaved = NullableDecimal(reader, "percentage_saved"),
        FailureCategory = NullableString(reader, "failure_category"),
        FailureMessage = NullableString(reader, "failure_message"),
        CreatedUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_utc")), CultureInfo.InvariantCulture),
        UpdatedUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_utc")), CultureInfo.InvariantCulture),
        CompletedUtc = NullableString(reader, "completed_utc") is { } completed ? DateTimeOffset.Parse(completed, CultureInfo.InvariantCulture) : null
    };

    private static string? NullableString(SqliteDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));
    private static int? NullableInt(SqliteDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetInt32(reader.GetOrdinal(column));
    private static long? NullableLong(SqliteDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetInt64(reader.GetOrdinal(column));
    private static bool? NullableBool(SqliteDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetInt32(reader.GetOrdinal(column)) != 0;
    private static decimal? NullableDecimal(SqliteDataReader reader, string column) => NullableString(reader, column) is { } value ? decimal.Parse(value, CultureInfo.InvariantCulture) : null;
}
