using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Infrastructure.Persistence;

/// <summary>
/// The jobs store. <paramref name="mode"/> is <see cref="SqliteOpenMode.ReadOnly"/> for a reader that must leave the file as
/// it found it (<c>calibrate</c>), paired with an initializer that migrates nothing; writes then fail.
/// </summary>
public sealed class SqliteJobRepository(IDatabaseInitializer databaseInitializer, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate) : IJobRepository
{
    private static readonly string TerminalStatuses = string.Join(", ", new[] { JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Skipped }.Select(status => ((int)status).ToString(CultureInfo.InvariantCulture)));

    public async Task<JobRecord?> FindOpenBySourceAsync(string databasePath, string sourcePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jobs WHERE source_path = $sourcePath COLLATE NOCASE AND status NOT IN ($completed, $failed, $cancelled, $skipped) ORDER BY created_utc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$sourcePath", sourcePath);
        command.Parameters.AddWithValue("$completed", (int)JobStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)JobStatus.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)JobStatus.Cancelled);
        command.Parameters.AddWithValue("$skipped", (int)JobStatus.Skipped);
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
        job.RowVersion = 0;
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        if (await ExecuteWriteAsync(connection, job, insert: true, cancellationToken) == 0) throw new OpenJobExistsException(job.SourcePath);
        return job;
    }

    public async Task UpdateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default)
    {
        var updatedUtc = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        var current = await GetStateAsync(connection, job.Id, cancellationToken);
        if (current is null) throw new InvalidOperationException($"Job {job.Id:N} does not exist.");
        // Stale before illegal: a copy read before a drop or another process's write is the writer's to pass over, not a bug.
        if (current.Value.RowVersion != job.RowVersion || current.Value.Held) throw new JobChangedException(job.Id);
        if (!JobStateTransitions.IsAllowed(current.Value.Status, job.Status)) throw new InvalidOperationException($"Cannot change job {job.Id:N} from {current.Value.Status} to {job.Status}.");
        var previous = (job.UpdatedUtc, job.RowVersion);
        job.UpdatedUtc = updatedUtc;
        if (await ExecuteWriteAsync(connection, job, insert: false, cancellationToken) == 0)
        {
            job.UpdatedUtc = previous.UpdatedUtc;
            throw new JobChangedException(job.Id);
        }
        job.RowVersion = previous.RowVersion + 1;
    }

    public Task<bool> SetHeldAsync(string databasePath, JobRecord job, bool held, CancellationToken cancellationToken = default) =>
        WriteQueueFieldsAsync(databasePath, job, "held = $held", command => command.Parameters.AddWithValue("$held", held ? 1 : 0), () => job.Held = held, cancellationToken);

    public Task<bool> RunNextAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default) =>
        WriteQueueFieldsAsync(databasePath, job, $"held = 0, priority = (SELECT COALESCE(MAX(priority), 0) + 1 FROM jobs WHERE status NOT IN ({TerminalStatuses}))", _ => { }, () => job.Held = false, cancellationToken);

    public Task<bool> DropAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default)
    {
        var completedUtc = DateTimeOffset.UtcNow;
        // Released too: a held flag on an ended job would mean nothing and could only mislead.
        return WriteQueueFieldsAsync(databasePath, job, "status = $cancelled, held = 0, failure_category = 'Cancelled', failure_message = 'Dropped by user.', completed_utc = $completedUtc", command =>
        {
            command.Parameters.AddWithValue("$cancelled", (int)JobStatus.Cancelled);
            command.Parameters.AddWithValue("$completedUtc", completedUtc.ToString("O"));
        }, () =>
        {
            job.Status = JobStatus.Cancelled;
            job.Held = false;
            job.FailureCategory = "Cancelled";
            job.FailureMessage = "Dropped by user.";
            job.CompletedUtc = completedUtc;
        }, cancellationToken);
    }

    /// <summary>A queue verb's write: only while the row is as the verb read it, so a run that started the job first wins.</summary>
    private async Task<bool> WriteQueueFieldsAsync(string databasePath, JobRecord job, string assignments, Action<SqliteCommand> parameters, Action applied, CancellationToken cancellationToken)
    {
        var updatedUtc = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE jobs SET {assignments}, updated_utc = $updatedUtc, row_version = row_version + 1 WHERE id = $id AND row_version = $rowVersion;";
        command.Parameters.AddWithValue("$id", job.Id.ToString("N"));
        command.Parameters.AddWithValue("$rowVersion", job.RowVersion);
        command.Parameters.AddWithValue("$updatedUtc", updatedUtc.ToString("O"));
        parameters(command);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        applied();
        job.UpdatedUtc = updatedUtc;
        job.RowVersion++;
        return true;
    }

    public async Task<IReadOnlyList<JobRecord>> ListAsync(string databasePath, bool terminal, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = terminal
            ? "SELECT * FROM jobs WHERE status IN ($completed, $failed, $cancelled, $skipped) ORDER BY updated_utc DESC;"
            : "SELECT * FROM jobs WHERE status NOT IN ($completed, $failed, $cancelled, $skipped) ORDER BY updated_utc DESC;";
        command.Parameters.AddWithValue("$completed", (int)JobStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)JobStatus.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)JobStatus.Cancelled);
        command.Parameters.AddWithValue("$skipped", (int)JobStatus.Skipped);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jobs = new List<JobRecord>();
        while (await reader.ReadAsync(cancellationToken)) jobs.Add(Read(reader));
        return jobs;
    }

    public async Task MarkActiveJobsInterruptedAsync(string databasePath, bool includeFinalizing, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE jobs
            SET resume_status = status, status = $interrupted, failure_category = 'Interrupted', failure_message = 'Application stopped before the job completed.', updated_utc = $updatedUtc, row_version = row_version + 1
            WHERE status IN ($staging, $crfSearching, $encoding, $downloading, $validating{(includeFinalizing ? ", $finalizing" : string.Empty)});
            """;
        command.Parameters.AddWithValue("$interrupted", (int)JobStatus.Interrupted);
        command.Parameters.AddWithValue("$crfSearching", (int)JobStatus.CrfSearching);
        command.Parameters.AddWithValue("$staging", (int)JobStatus.Staging);
        command.Parameters.AddWithValue("$encoding", (int)JobStatus.Encoding);
        command.Parameters.AddWithValue("$downloading", (int)JobStatus.Downloading);
        command.Parameters.AddWithValue("$validating", (int)JobStatus.Validating);
        if (includeFinalizing) command.Parameters.AddWithValue("$finalizing", (int)JobStatus.Finalizing);
        command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<JobRecord?> FindOpenByImmichAssetAsync(string databasePath, string assetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jobs WHERE immich_asset_id = $assetId AND status NOT IN ($completed, $failed, $cancelled, $skipped) ORDER BY created_utc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$assetId", assetId);
        command.Parameters.AddWithValue("$completed", (int)JobStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)JobStatus.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)JobStatus.Cancelled);
        command.Parameters.AddWithValue("$skipped", (int)JobStatus.Skipped);
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
        command.CommandText = "SELECT * FROM immich_skipped_assets;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var skips = new Dictionary<string, ImmichSkipRecord>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var skip = new ImmichSkipRecord(
                reader.GetString(reader.GetOrdinal("asset_id")),
                reader.GetString(reader.GetOrdinal("checksum")),
                reader.GetString(reader.GetOrdinal("rules_fingerprint")),
                reader.GetString(reader.GetOrdinal("reason")),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("recorded_utc")), CultureInfo.InvariantCulture),
                ReadVideoFacts(reader));
            skips[skip.AssetId] = skip;
        }

        return skips;
    }

    public async Task PutImmichSkipAsync(string databasePath, ImmichSkipRecord skip, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO immich_skipped_assets (asset_id, checksum, rules_fingerprint, reason, recorded_utc, video_codec, video_width, video_height, video_bitrate, video_duration_seconds, video_size_bytes)
            VALUES ($assetId, $checksum, $rulesFingerprint, $reason, $recordedUtc, $videoCodec, $videoWidth, $videoHeight, $videoBitrate, $videoDurationSeconds, $videoSizeBytes)
            ON CONFLICT(asset_id) DO UPDATE SET checksum = excluded.checksum, rules_fingerprint = excluded.rules_fingerprint, reason = excluded.reason, recorded_utc = excluded.recorded_utc,
                video_codec = excluded.video_codec, video_width = excluded.video_width, video_height = excluded.video_height, video_bitrate = excluded.video_bitrate,
                video_duration_seconds = excluded.video_duration_seconds, video_size_bytes = excluded.video_size_bytes;
            """;
        AddVideoFacts(command, skip.VideoFacts);
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
        var builder = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath), Mode = mode, ForeignKeys = true, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <returns>The rows written: 0 when an open job blocked the insert, or the update lost its compare-and-swap.</returns>
    private static async Task<int> ExecuteWriteAsync(SqliteConnection connection, JobRecord job, bool insert, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = insert
            // One statement, so two commands queueing the same original at once cannot both insert an open job for it.
            ? $"""INSERT INTO jobs (id, source_path, source_fingerprint, status, resume_status, attempt, crf, output_path, manifest_path, validation_passed, source_size_bytes, output_size_bytes, percentage_saved, failure_category, failure_message, created_utc, updated_utc, completed_utc, execution_mode, remote_host, remote_workspace, source_kind, immich_asset_id, immich_checksum, immich_new_asset_id, quality_fingerprint, video_codec, video_width, video_height, video_bitrate, video_duration_seconds, video_size_bytes, forced) SELECT $id, $sourcePath, $sourceFingerprint, $status, $resumeStatus, $attempt, $crf, $outputPath, $manifestPath, $validationPassed, $sourceSizeBytes, $outputSizeBytes, $percentageSaved, $failureCategory, $failureMessage, $createdUtc, $updatedUtc, $completedUtc, $executionMode, $remoteHost, $remoteWorkspace, $sourceKind, $immichAssetId, $immichChecksum, $immichNewAssetId, $qualityFingerprint, $videoCodec, $videoWidth, $videoHeight, $videoBitrate, $videoDurationSeconds, $videoSizeBytes, $forced WHERE NOT EXISTS (SELECT 1 FROM jobs WHERE status NOT IN ({TerminalStatuses}) AND (source_path = $sourcePath COLLATE NOCASE OR ($immichAssetId IS NOT NULL AND immich_asset_id = $immichAssetId)));"""
            : """UPDATE jobs SET source_path = $sourcePath, source_fingerprint = $sourceFingerprint, status = $status, resume_status = $resumeStatus, attempt = $attempt, crf = $crf, output_path = $outputPath, manifest_path = $manifestPath, validation_passed = $validationPassed, source_size_bytes = $sourceSizeBytes, output_size_bytes = $outputSizeBytes, percentage_saved = $percentageSaved, failure_category = $failureCategory, failure_message = $failureMessage, updated_utc = $updatedUtc, completed_utc = $completedUtc, execution_mode = $executionMode, remote_host = $remoteHost, remote_workspace = $remoteWorkspace, source_kind = $sourceKind, immich_asset_id = $immichAssetId, immich_checksum = $immichChecksum, immich_new_asset_id = $immichNewAssetId, quality_fingerprint = $qualityFingerprint, video_codec = $videoCodec, video_width = $videoWidth, video_height = $videoHeight, video_bitrate = $videoBitrate, video_duration_seconds = $videoDurationSeconds, video_size_bytes = $videoSizeBytes, row_version = row_version + 1 WHERE id = $id AND row_version = $rowVersion AND held = 0;""";
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
        Add(command, "$qualityFingerprint", job.QualityFingerprint);
        AddVideoFacts(command, job.VideoFacts);
        Add(command, "$forced", job.Forced ? 1 : 0);
        Add(command, "$rowVersion", job.RowVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddVideoFacts(SqliteCommand command, VideoFacts? facts)
    {
        Add(command, "$videoCodec", facts?.Codec);
        Add(command, "$videoWidth", facts?.Width);
        Add(command, "$videoHeight", facts?.Height);
        Add(command, "$videoBitrate", facts?.Bitrate);
        Add(command, "$videoDurationSeconds", facts?.DurationSeconds);
        Add(command, "$videoSizeBytes", facts?.SizeBytes);
    }

    // The codec and size are always written together, so a row with either missing predates migration 8.
    private static VideoFacts? ReadVideoFacts(SqliteDataReader reader) =>
        NullableString(reader, "video_codec") is { } codec && NullableLong(reader, "video_size_bytes") is { } size
            ? new VideoFacts(codec, NullableInt(reader, "video_width"), NullableInt(reader, "video_height"), NullableLong(reader, "video_bitrate"), NullableDouble(reader, "video_duration_seconds"), size)
            : null;

    private static async Task<(JobStatus Status, long RowVersion, bool Held)?> GetStateAsync(SqliteConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, row_version, held FROM jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ((JobStatus)reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2) != 0) : null;
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
        ImmichNewAssetId = NullableString(reader, "immich_new_asset_id"),
        QualityFingerprint = NullableString(reader, "quality_fingerprint"),
        VideoFacts = ReadVideoFacts(reader),
        Held = reader.GetInt32(reader.GetOrdinal("held")) != 0,
        Priority = reader.GetInt32(reader.GetOrdinal("priority")),
        Forced = reader.GetInt32(reader.GetOrdinal("forced")) != 0,
        RowVersion = reader.GetInt64(reader.GetOrdinal("row_version"))
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
    private static double? NullableDouble(SqliteDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetDouble(reader.GetOrdinal(column));
    private static bool? NullableBool(SqliteDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetInt32(reader.GetOrdinal(column)) != 0;
    private static decimal? NullableDecimal(SqliteDataReader reader, string column) => NullableString(reader, column) is { } value ? decimal.Parse(value, CultureInfo.InvariantCulture) : null;
}
