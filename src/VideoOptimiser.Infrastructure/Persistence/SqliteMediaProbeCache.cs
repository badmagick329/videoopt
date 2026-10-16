using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.Infrastructure.Persistence;

/// <summary>
/// ffprobe results keyed by path. <paramref name="mode"/> is <see cref="SqliteOpenMode.ReadOnly"/> for a reader that must
/// leave the file as it found it (<c>calibrate</c>), paired with an initializer that migrates nothing; stores then fail.
/// </summary>
public sealed class SqliteMediaProbeCache(IDatabaseInitializer databaseInitializer, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate) : IMediaProbeCache
{
    // Bump when the probe reads a fact differently, so rows cached by the old reading are probed again.
    // 2: a video stream without its own bitrate takes the whole file's. A version 1 row that has a bitrate reads the same
    // under 2, so LoadAllAsync keeps it: only the rows without one are probed again, not the whole library.
    public const int CacheSchemaVersion = 2;

    public async Task<IReadOnlyList<MediaProbeCacheEntry>> LoadAllAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_path, source_size_bytes, source_last_write_utc_ticks,
                   primary_video_codec, video_stream_count, audio_stream_count, subtitle_stream_count, attachment_count,
                   duration_seconds, size_bytes, primary_video_width, primary_video_height, primary_video_bitrate
            FROM media_probe_cache
            WHERE cache_schema_version = $cacheSchemaVersion
               OR (cache_schema_version = 1 AND primary_video_bitrate IS NOT NULL);
            """;
        command.Parameters.AddWithValue("$cacheSchemaVersion", CacheSchemaVersion);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<MediaProbeCacheEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new MediaProbeCacheEntry(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                new MediaInfo(
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    NullableDouble(reader, 8),
                    NullableLong(reader, 9),
                    NullableInt(reader, 10),
                    NullableInt(reader, 11),
                    NullableLong(reader, 12))));
        }
        return entries;
    }

    public async Task StoreAsync(string databasePath, MediaProbeCacheEntry entry, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO media_probe_cache (
                source_path, source_size_bytes, source_last_write_utc_ticks,
                primary_video_codec, video_stream_count, audio_stream_count, subtitle_stream_count, attachment_count,
                duration_seconds, size_bytes, primary_video_width, primary_video_height, primary_video_bitrate,
                cache_schema_version, updated_utc)
            VALUES (
                $sourcePath, $sourceSizeBytes, $sourceLastWriteUtcTicks,
                $primaryVideoCodec, $videoStreamCount, $audioStreamCount, $subtitleStreamCount, $attachmentCount,
                $durationSeconds, $sizeBytes, $primaryVideoWidth, $primaryVideoHeight, $primaryVideoBitrate,
                $cacheSchemaVersion, $updatedUtc)
            ON CONFLICT(source_path) DO UPDATE SET
                source_size_bytes = excluded.source_size_bytes,
                source_last_write_utc_ticks = excluded.source_last_write_utc_ticks,
                primary_video_codec = excluded.primary_video_codec,
                video_stream_count = excluded.video_stream_count,
                audio_stream_count = excluded.audio_stream_count,
                subtitle_stream_count = excluded.subtitle_stream_count,
                attachment_count = excluded.attachment_count,
                duration_seconds = excluded.duration_seconds,
                size_bytes = excluded.size_bytes,
                primary_video_width = excluded.primary_video_width,
                primary_video_height = excluded.primary_video_height,
                primary_video_bitrate = excluded.primary_video_bitrate,
                cache_schema_version = excluded.cache_schema_version,
                updated_utc = excluded.updated_utc;
            """;
        Add(command, "$sourcePath", entry.SourcePath);
        Add(command, "$sourceSizeBytes", entry.SourceSizeBytes);
        Add(command, "$sourceLastWriteUtcTicks", entry.SourceLastWriteUtcTicks);
        Add(command, "$primaryVideoCodec", entry.MediaInfo.PrimaryVideoCodec);
        Add(command, "$videoStreamCount", entry.MediaInfo.VideoStreamCount);
        Add(command, "$audioStreamCount", entry.MediaInfo.AudioStreamCount);
        Add(command, "$subtitleStreamCount", entry.MediaInfo.SubtitleStreamCount);
        Add(command, "$attachmentCount", entry.MediaInfo.AttachmentCount);
        Add(command, "$durationSeconds", entry.MediaInfo.DurationSeconds);
        Add(command, "$sizeBytes", entry.MediaInfo.SizeBytes);
        Add(command, "$primaryVideoWidth", entry.MediaInfo.PrimaryVideoWidth);
        Add(command, "$primaryVideoHeight", entry.MediaInfo.PrimaryVideoHeight);
        Add(command, "$primaryVideoBitrate", entry.MediaInfo.PrimaryVideoBitrate);
        Add(command, "$cacheSchemaVersion", CacheSchemaVersion);
        Add(command, "$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(string databasePath, CancellationToken cancellationToken)
    {
        await databaseInitializer.InitializeAsync(databasePath, cancellationToken);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = mode,
            ForeignKeys = true,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static int? NullableInt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static long? NullableLong(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static double? NullableDouble(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
