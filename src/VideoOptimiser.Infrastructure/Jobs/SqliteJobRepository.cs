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
            WHERE status IN ($staging, $crfSearching, $encoding, $downloading, $validating, $finalizing);
            """;
        command.Parameters.AddWithValue("$interrupted", (int)JobStatus.Interrupted);
        command.Parameters.AddWithValue("$crfSearching", (int)JobStatus.CrfSearching);
        command.Parameters.AddWithValue("$staging", (int)JobStatus.Staging);
        command.Parameters.AddWithValue("$encoding", (int)JobStatus.Encoding);
        command.Parameters.AddWithValue("$downloading", (int)JobStatus.Downloading);
        command.Parameters.AddWithValue("$validating", (int)JobStatus.Validating);
        command.Parameters.AddWithValue("$finalizing", (int)JobStatus.Finalizing);
        command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<JobRecord?> FindOpenByImmichAssetAsync(string databasePath, string assetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jobs WHERE immich_asset_id = $assetId AND status NOT IN ($completed, $failed, $cancelled) ORDER BY created_utc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$assetId", assetId);
        command.Parameters.AddWithValue("$completed", (int)JobStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)JobStatus.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)JobStatus.Cancelled);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<JobRecord>> ListCompletedImmichAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jobs WHERE source_kind = 'immich' AND status = $completed ORDER BY completed_utc;";
        command.Parameters.AddWithValue("$completed", (int)JobStatus.Completed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jobs = new List<JobRecord>();
        while (await reader.ReadAsync(cancellationToken)) jobs.Add(Read(reader));
        return jobs;
    }

    public async Task<IReadOnlyDictionary<string, ImmichSkipRecord>> ListImmichSkipsAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT asset_id, checksum, rules_fingerprint, reason, recorded_utc FROM immich_skipped_assets;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var skips = new Dictionary<string, ImmichSkipRecord>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var skip = new ImmichSkipRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));
            skips[skip.AssetId] = skip;
        }

        return skips;
    }

    public async Task PutImmichSkipAsync(string databasePath, ImmichSkipRecord skip, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO immich_skipped_assets (asset_id, checksum, rules_fingerprint, reason, recorded_utc)
            VALUES ($assetId, $checksum, $rulesFingerprint, $reason, $recordedUtc)
            ON CONFLICT(asset_id) DO UPDATE SET checksum = excluded.checksum, rules_fingerprint = excluded.rules_fingerprint, reason = excluded.reason, recorded_utc = excluded.recorded_utc;
            """;
        command.Parameters.AddWithValue("$assetId", skip.AssetId);
        command.Parameters.AddWithValue("$checksum", skip.Checksum);
        command.Parameters.AddWithValue("$rulesFingerprint", skip.RulesFingerprint);
        command.Parameters.AddWithValue("$reason", skip.Reason);
        command.Parameters.AddWithValue("$recordedUtc", skip.RecordedUtc.ToString("O"));
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
            ? """INSERT INTO jobs (id, source_path, source_fingerprint, status, resume_status, attempt, crf, output_path, manifest_path, validation_passed, source_size_bytes, output_size_bytes, percentage_saved, failure_category, failure_message, created_utc, updated_utc, completed_utc, execution_mode, remote_host, remote_workspace, source_kind, immich_asset_id, immich_checksum, immich_new_asset_id) VALUES ($id, $sourcePath, $sourceFingerprint, $status, $resumeStatus, $attempt, $crf, $outputPath, $manifestPath, $validationPassed, $sourceSizeBytes, $outputSizeBytes, $percentageSaved, $failureCategory, $failureMessage, $createdUtc, $updatedUtc, $completedUtc, $executionMode, $remoteHost, $remoteWorkspace, $sourceKind, $immichAssetId, $immichChecksum, $immichNewAssetId);"""
            : """UPDATE jobs SET source_path = $sourcePath, source_fingerprint = $sourceFingerprint, status = $status, resume_status = $resumeStatus, attempt = $attempt, crf = $crf, output_path = $outputPath, manifest_path = $manifestPath, validation_passed = $validationPassed, source_size_bytes = $sourceSizeBytes, output_size_bytes = $outputSizeBytes, percentage_saved = $percentageSaved, failure_category = $failureCategory, failure_message = $failureMessage, updated_utc = $updatedUtc, completed_utc = $completedUtc, execution_mode = $executionMode, remote_host = $remoteHost, remote_workspace = $remoteWorkspace, source_kind = $sourceKind, immich_asset_id = $immichAssetId, immich_checksum = $immichChecksum, immich_new_asset_id = $immichNewAssetId WHERE id = $id;""";
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
        Add(command, "$executionMode", job.ExecutionMode);
        Add(command, "$remoteHost", job.RemoteHost);
        Add(command, "$remoteWorkspace", job.RemoteWorkspace);
        Add(command, "$sourceKind", SourceKindText(job.SourceKind));
        Add(command, "$immichAssetId", job.ImmichAssetId);
        Add(command, "$immichChecksum", job.ImmichChecksum);
        Add(command, "$immichNewAssetId", job.ImmichNewAssetId);
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
        CompletedUtc = NullableString(reader, "completed_utc") is { } completed ? DateTimeOffset.Parse(completed, CultureInfo.InvariantCulture) : null,
        ExecutionMode = NullableString(reader, "execution_mode") ?? "local",
        RemoteHost = NullableString(reader, "remote_host"),
        RemoteWorkspace = NullableString(reader, "remote_workspace"),
        SourceKind = ParseSourceKind(reader.GetString(reader.GetOrdinal("source_kind"))),
        ImmichAssetId = NullableString(reader, "immich_asset_id"),
        ImmichChecksum = NullableString(reader, "immich_checksum"),
        ImmichNewAssetId = NullableString(reader, "immich_new_asset_id")
    };

    private static string SourceKindText(JobSourceKind kind) => kind switch
    {
        JobSourceKind.File => "file",
        JobSourceKind.Immich => "immich",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    // An unknown value means a newer version wrote this row; guessing "file" would treat an Immich staged copy as a user's original.
    private static JobSourceKind ParseSourceKind(string value) => value switch
    {
        "file" => JobSourceKind.File,
        "immich" => JobSourceKind.Immich,
        _ => throw new InvalidOperationException($"The job database holds an unknown source kind '{value}'.")
    };

    private static string? NullableString(SqliteDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));
    private static int? NullableInt(SqliteDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetInt32(reader.GetOrdinal(column));
    private static long? NullableLong(SqliteDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetInt64(reader.GetOrdinal(column));
    private static bool? NullableBool(SqliteDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetInt32(reader.GetOrdinal(column)) != 0;
    private static decimal? NullableDecimal(SqliteDataReader reader, string column) => NullableString(reader, column) is { } value ? decimal.Parse(value, CultureInfo.InvariantCulture) : null;
}
