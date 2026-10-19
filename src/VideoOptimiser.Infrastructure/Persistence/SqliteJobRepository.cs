using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Infrastructure.Persistence;

/// <summary>
/// The jobs store. <paramref name="mode"/> is <see cref="SqliteOpenMode.ReadOnly"/> for a reader that must leave the file as
/// it found it (<c>calibrate</c>), paired with an initializer that migrates nothing; writes then fail.
/// </summary>
public sealed class SqliteJobRepository(IDatabaseInitializer databaseInitializer, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate) : IJobRepository
{
    private static readonly string TerminalStatuses = string.Join(", ", new[] { JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Skipped, JobStatus.Rejected }.Select(status => ((int)status).ToString(CultureInfo.InvariantCulture)));

    public async Task<JobRecord?> FindOpenBySourceAsync(string databasePath, string sourcePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {JobsTable.Select} FROM jobs WHERE source_path = $sourcePath COLLATE NOCASE AND status NOT IN ({TerminalStatuses}) ORDER BY created_utc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$sourcePath", sourcePath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<JobRecord?> GetAsync(string databasePath, Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {JobsTable.Select} FROM jobs WHERE id = $id;";
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
        if (job.Status != JobStatus.Failed) job.DismissedUtc = null;
    }

    public Task<bool> SetHeldAsync(string databasePath, JobRecord job, bool held, CancellationToken cancellationToken = default) =>
        WriteQueueFieldsAsync(databasePath, job, "held = $held", command => command.Parameters.AddWithValue("$held", held ? 1 : 0), () => job.Held = held, cancellationToken);

    public Task<bool> RunNextAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default) =>
        WriteQueueFieldsAsync(databasePath, job, $"held = 0, priority = (SELECT COALESCE(MAX(priority), 0) + 1 FROM jobs WHERE status NOT IN ({TerminalStatuses}))", _ => { }, () => job.Held = false, cancellationToken);

    public Task<bool> DropAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default)
    {
        var completedUtc = DateTimeOffset.UtcNow;
        // Released too: a held flag on an ended job would mean nothing and could only mislead.
        return WriteQueueFieldsAsync(databasePath, job, "status = $cancelled, held = 0, failure_category = $category, failure_message = 'Dropped by user.', completed_utc = $completedUtc", command =>
        {
            command.Parameters.AddWithValue("$cancelled", (int)JobStatus.Cancelled);
            command.Parameters.AddWithValue("$category", FailureCategories.Cancelled);
            command.Parameters.AddWithValue("$completedUtc", completedUtc.ToString("O"));
        }, () =>
        {
            job.Status = JobStatus.Cancelled;
            job.Held = false;
            job.FailureCategory = FailureCategories.Cancelled;
            job.FailureMessage = "Dropped by user.";
            job.CompletedUtc = completedUtc;
        }, cancellationToken);
    }

    public Task<bool> RejectAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default)
    {
        var completedUtc = DateTimeOffset.UtcNow;
        return WriteQueueFieldsAsync(databasePath, job, "status = $rejected, held = 0, failure_category = $category, failure_message = $message, completed_utc = $completedUtc", command =>
        {
            command.Parameters.AddWithValue("$rejected", (int)JobStatus.Rejected);
            command.Parameters.AddWithValue("$category", FailureCategories.Rejected);
            command.Parameters.AddWithValue("$message", RejectedMessage);
            command.Parameters.AddWithValue("$completedUtc", completedUtc.ToString("O"));
        }, () =>
        {
            job.Status = JobStatus.Rejected;
            job.Held = false;
            job.FailureCategory = FailureCategories.Rejected;
            job.FailureMessage = RejectedMessage;
            job.CompletedUtc = completedUtc;
        }, cancellationToken);
    }

    private const string RejectedMessage = "Rejected by user.";

    // Not through WriteQueueFieldsAsync: that moves updated_utc, and history sorts on it, so a dismissed old failure would jump to the top.
    public async Task<bool> DismissAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default)
    {
        var dismissedUtc = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET dismissed_utc = $dismissedUtc, row_version = row_version + 1 WHERE id = $id AND row_version = $rowVersion AND status = $failed;";
        command.Parameters.AddWithValue("$id", job.Id.ToString("N"));
        command.Parameters.AddWithValue("$rowVersion", job.RowVersion);
        command.Parameters.AddWithValue("$failed", (int)JobStatus.Failed);
        command.Parameters.AddWithValue("$dismissedUtc", dismissedUtc.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        job.DismissedUtc = dismissedUtc;
        job.RowVersion++;
        return true;
    }

    public async Task<int> DismissAllFailedAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET dismissed_utc = $dismissedUtc, row_version = row_version + 1 WHERE status = $failed AND dismissed_utc IS NULL;";
        command.Parameters.AddWithValue("$failed", (int)JobStatus.Failed);
        command.Parameters.AddWithValue("$dismissedUtc", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
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
        command.CommandText = $"SELECT {JobsTable.Select} FROM jobs WHERE status {(terminal ? "IN" : "NOT IN")} ({TerminalStatuses}) ORDER BY updated_utc DESC;";
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
            SET resume_status = status, status = $interrupted, failure_category = $category, failure_message = 'Application stopped before the job completed.', updated_utc = $updatedUtc, row_version = row_version + 1
            WHERE status IN ($staging, $crfSearching, $encoding, $downloading, $validating{(includeFinalizing ? ", $finalizing" : string.Empty)});
            """;
        command.Parameters.AddWithValue("$interrupted", (int)JobStatus.Interrupted);
        command.Parameters.AddWithValue("$category", FailureCategories.Interrupted);
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
        command.CommandText = $"SELECT {JobsTable.Select} FROM jobs WHERE immich_asset_id = $assetId AND status NOT IN ({TerminalStatuses}) ORDER BY created_utc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$assetId", assetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<JobRecord>> ListCompletedImmichAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {JobsTable.Select} FROM jobs WHERE source_kind = 'immich' AND status = $completed ORDER BY completed_utc;";
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

    private Task<SqliteConnection> OpenAsync(string databasePath, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder { Mode = mode, ForeignKeys = true, Pooling = false };
        return SqliteStoreConnection.OpenAsync(databaseInitializer, databasePath, builder, cancellationToken);
    }

    /// <returns>The rows written: 0 when an open job blocked the insert, or the update lost its compare-and-swap.</returns>
    private static async Task<int> ExecuteWriteAsync(SqliteConnection connection, JobRecord job, bool insert, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = insert ? JobsTable.Insert : JobsTable.Update;
        // Every bound column, for either statement: the UPDATE's WHERE needs $id, which its SET leaves out.
        foreach (var column in JobsTable.Columns)
        {
            if (column.Bind is { } bind) Add(command, column.Parameter, bind(job));
        }

        Add(command, "$failed", (int)JobStatus.Failed);
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

    // The codec and size are always written together, so a row with either missing predates migration 8. By name: both the
    // jobs and immich_skipped_assets tables hold these columns.
    private static VideoFacts? ReadVideoFacts(SqliteDataReader reader)
    {
        int At(string column) => reader.GetOrdinal(column);
        return NullableString(reader, At("video_codec")) is { } codec && NullableLong(reader, At("video_size_bytes")) is { } size
            ? new VideoFacts(codec, NullableInt(reader, At("video_width")), NullableInt(reader, At("video_height")), NullableLong(reader, At("video_bitrate")), NullableDouble(reader, At("video_duration_seconds")), size)
            : null;
    }

    private static async Task<(JobStatus Status, long RowVersion, bool Held)?> GetStateAsync(SqliteConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, row_version, held FROM jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ((JobStatus)reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2) != 0) : null;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    /// <summary>Reads a row selected with <see cref="JobsTable.Select"/>; by name, so the table's order is free to change.</summary>
    private static JobRecord Read(SqliteDataReader reader)
    {
        var job = new JobRecord();
        foreach (var column in JobsTable.Columns) column.Read?.Invoke(reader, reader.GetOrdinal(column.Name), job);
        return job;
    }

    /// <summary>Which of <see cref="ExecuteWriteAsync"/>'s statements set a column.</summary>
    [Flags]
    private enum JobColumnWrites
    {
        None = 0,
        Insert = 1,
        Update = 2,
        Both = Insert | Update
    }

    /// <summary>One <c>jobs</c> column. Its parameter is <c>$</c> and its name.</summary>
    /// <param name="Bind">The parameter's value from the record; null when nothing binds it (an expression or another method writes it).</param>
    /// <param name="Read">Sets the record's property from the column at the ordinal given; null when another column's entry reads it.</param>
    /// <param name="UpdateExpression">What the UPDATE sets the column to instead of its parameter.</param>
    private sealed record JobColumn(string Name, JobColumnWrites Writes, Func<JobRecord, object?>? Bind, Action<SqliteDataReader, int, JobRecord>? Read, string? UpdateExpression = null)
    {
        public string Parameter => "$" + Name;
    }

    /// <summary>
    /// The single list of the <c>jobs</c> columns a <see cref="JobRecord"/> maps to. The INSERT, the UPDATE's SET, the
    /// parameters, every <c>SELECT</c> of a whole job and <see cref="SqliteJobRepository.Read"/> come from it, so adding a
    /// column is one entry here plus its migration. A nested class so its statics need nothing declared after them.
    /// </summary>
    private static class JobsTable
    {
        public static readonly JobColumn[] Columns =
        [
            // Created once: an UPDATE neither moves a job's identity nor its creation, and only queue add --force sets forced.
            new("id", JobColumnWrites.Insert, job => job.Id.ToString("N"), (reader, i, job) => job.Id = Guid.ParseExact(reader.GetString(i), "N")),
            new("created_utc", JobColumnWrites.Insert, job => job.CreatedUtc.ToString("O"), (reader, i, job) => job.CreatedUtc = ParseUtc(reader.GetString(i))),
            new("forced", JobColumnWrites.Insert, job => job.Forced ? 1 : 0, (reader, i, job) => job.Forced = reader.GetInt32(i) != 0),
            new("source_path", JobColumnWrites.Both, job => job.SourcePath, (reader, i, job) => job.SourcePath = reader.GetString(i)),
            new("source_fingerprint", JobColumnWrites.Both, job => job.SourceFingerprint, (reader, i, job) => job.SourceFingerprint = reader.GetString(i)),
            new("status", JobColumnWrites.Both, job => (int)job.Status, (reader, i, job) => job.Status = (JobStatus)reader.GetInt32(i)),
            new("resume_status", JobColumnWrites.Both, job => job.ResumeStatus is null ? null : (int)job.ResumeStatus.Value, (reader, i, job) => job.ResumeStatus = NullableInt(reader, i) is { } status ? (JobStatus)status : null),
            new("attempt", JobColumnWrites.Both, job => job.Attempt, (reader, i, job) => job.Attempt = reader.GetInt32(i)),
            new("crf", JobColumnWrites.Both, job => job.Crf, (reader, i, job) => job.Crf = NullableInt(reader, i)),
            new("output_path", JobColumnWrites.Both, job => job.OutputPath, (reader, i, job) => job.OutputPath = NullableString(reader, i)),
            new("manifest_path", JobColumnWrites.Both, job => job.ManifestPath, (reader, i, job) => job.ManifestPath = NullableString(reader, i)),
            new("validation_passed", JobColumnWrites.Both, job => job.ValidationPassed is null ? null : job.ValidationPassed.Value ? 1 : 0, (reader, i, job) => job.ValidationPassed = NullableInt(reader, i) is { } passed ? passed != 0 : null),
            // One warning per line: validation words each as a single line. None stores as null.
            new("validation_warnings", JobColumnWrites.Both, job => JoinLines(job.ValidationWarnings), (reader, i, job) => job.ValidationWarnings = SplitLines(reader, i)),
            new("notes", JobColumnWrites.Both, job => JoinLines(job.Notes), (reader, i, job) => job.Notes = SplitLines(reader, i)),
            new("source_size_bytes", JobColumnWrites.Both, job => job.SourceSizeBytes, (reader, i, job) => job.SourceSizeBytes = NullableLong(reader, i)),
            new("output_size_bytes", JobColumnWrites.Both, job => job.OutputSizeBytes, (reader, i, job) => job.OutputSizeBytes = NullableLong(reader, i)),
            new("percentage_saved", JobColumnWrites.Both, job => job.PercentageSaved?.ToString(CultureInfo.InvariantCulture), (reader, i, job) => job.PercentageSaved = NullableString(reader, i) is { } saved ? decimal.Parse(saved, CultureInfo.InvariantCulture) : null),
            new("failure_category", JobColumnWrites.Both, job => job.FailureCategory, (reader, i, job) => job.FailureCategory = NullableString(reader, i)),
            new("failure_message", JobColumnWrites.Both, job => job.FailureMessage, (reader, i, job) => job.FailureMessage = NullableString(reader, i)),
            new("updated_utc", JobColumnWrites.Both, job => job.UpdatedUtc.ToString("O"), (reader, i, job) => job.UpdatedUtc = ParseUtc(reader.GetString(i))),
            new("completed_utc", JobColumnWrites.Both, job => job.CompletedUtc?.ToString("O"), (reader, i, job) => job.CompletedUtc = NullableString(reader, i) is { } completed ? ParseUtc(completed) : null),
            new("execution_mode", JobColumnWrites.Both, job => job.ExecutionMode, (reader, i, job) => job.ExecutionMode = NullableString(reader, i) ?? ProcessingModes.Local),
            new("remote_host", JobColumnWrites.Both, job => job.RemoteHost, (reader, i, job) => job.RemoteHost = NullableString(reader, i)),
            new("remote_workspace", JobColumnWrites.Both, job => job.RemoteWorkspace, (reader, i, job) => job.RemoteWorkspace = NullableString(reader, i)),
            new("source_kind", JobColumnWrites.Both, job => SourceKindText(job.SourceKind), (reader, i, job) => job.SourceKind = ParseSourceKind(reader.GetString(i))),
            new("immich_asset_id", JobColumnWrites.Both, job => job.ImmichAssetId, (reader, i, job) => job.ImmichAssetId = NullableString(reader, i)),
            new("immich_checksum", JobColumnWrites.Both, job => job.ImmichChecksum, (reader, i, job) => job.ImmichChecksum = NullableString(reader, i)),
            new("immich_new_asset_id", JobColumnWrites.Both, job => job.ImmichNewAssetId, (reader, i, job) => job.ImmichNewAssetId = NullableString(reader, i)),
            new("quality_fingerprint", JobColumnWrites.Both, job => job.QualityFingerprint, (reader, i, job) => job.QualityFingerprint = NullableString(reader, i)),
            // The video facts are six columns and one property: the codec's entry reads all six.
            new("video_codec", JobColumnWrites.Both, job => job.VideoFacts?.Codec, (reader, _, job) => job.VideoFacts = ReadVideoFacts(reader)),
            new("video_width", JobColumnWrites.Both, job => job.VideoFacts?.Width, null),
            new("video_height", JobColumnWrites.Both, job => job.VideoFacts?.Height, null),
            new("video_bitrate", JobColumnWrites.Both, job => job.VideoFacts?.Bitrate, null),
            new("video_duration_seconds", JobColumnWrites.Both, job => job.VideoFacts?.DurationSeconds, null),
            new("video_size_bytes", JobColumnWrites.Both, job => job.VideoFacts?.SizeBytes, null),
            // The queue verbs alone write these (WriteQueueFieldsAsync), so a run's copy can never undo a hold or a "run next".
            new("held", JobColumnWrites.None, null, (reader, i, job) => job.Held = reader.GetInt32(i) != 0),
            new("priority", JobColumnWrites.None, null, (reader, i, job) => job.Priority = reader.GetInt32(i)),
            // Set by the dismiss verbs. A dismissal is of a failure: a job that leaves Failed (a resumed finalisation) and fails again shows again.
            new("dismissed_utc", JobColumnWrites.Update, null, (reader, i, job) => job.DismissedUtc = NullableString(reader, i) is { } dismissed ? ParseUtc(dismissed) : null, "CASE WHEN $status = $failed THEN dismissed_utc END"),
            // The compare-and-swap's version: the insert leaves the column's default of 0, and every write raises it.
            new("row_version", JobColumnWrites.Update, null, (reader, i, job) => job.RowVersion = reader.GetInt64(i), "row_version + 1")
        ];

        /// <summary>The column list of every <c>SELECT</c> that <see cref="SqliteJobRepository.Read"/> reads.</summary>
        public static readonly string Select = string.Join(", ", Columns.Select(column => column.Name));

        private static readonly JobColumn[] Inserted = [.. Columns.Where(column => column.Writes.HasFlag(JobColumnWrites.Insert))];

        // One statement, so two commands queueing the same original at once cannot both insert an open job for it.
        public static readonly string Insert =
            $"INSERT INTO jobs ({string.Join(", ", Inserted.Select(column => column.Name))}) SELECT {string.Join(", ", Inserted.Select(column => column.Parameter))} " +
            $"WHERE NOT EXISTS (SELECT 1 FROM jobs WHERE status NOT IN ({TerminalStatuses}) AND (source_path = $source_path COLLATE NOCASE OR ($immich_asset_id IS NOT NULL AND immich_asset_id = $immich_asset_id)));";

        public static readonly string Update =
            $"UPDATE jobs SET {string.Join(", ", Columns.Where(column => column.Writes.HasFlag(JobColumnWrites.Update)).Select(column => $"{column.Name} = {column.UpdateExpression ?? column.Parameter}"))} " +
            "WHERE id = $id AND row_version = $rowVersion AND held = 0;";
    }

    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static string? JoinLines(IReadOnlyList<string> lines) => lines.Count == 0 ? null : string.Join('\n', lines);

    private static string[] SplitLines(SqliteDataReader reader, int ordinal) => NullableString(reader, ordinal)?.Split('\n') ?? [];

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

    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static int? NullableInt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static long? NullableLong(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static double? NullableDouble(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
