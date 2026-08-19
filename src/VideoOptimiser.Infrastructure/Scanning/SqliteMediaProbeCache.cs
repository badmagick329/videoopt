using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Application.Scanning;

namespace VideoOptimiser.Infrastructure.Scanning;

public sealed class SqliteMediaProbeCache(IDatabaseInitializer databaseInitializer) : IMediaProbeCache
{
    public const int CacheSchemaVersion = 1;

    public async Task<MediaInfo?> GetAsync(
        string databasePath,
        string sourcePath,
        long sourceSizeBytes,
        long sourceLastWriteUtcTicks,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT primary_video_codec, video_stream_count, audio_stream_count, subtitle_stream_count, attachment_count,
                   duration_seconds, size_bytes, primary_video_width, primary_video_height, primary_video_bitrate
            FROM media_probe_cache
            WHERE source_path = $sourcePath COLLATE NOCASE
              AND source_size_bytes = $sourceSizeBytes
              AND source_last_write_utc_ticks = $sourceLastWriteUtcTicks
              AND cache_schema_version = $cacheSchemaVersion;
            """;
        command.Parameters.AddWithValue("$sourcePath", sourcePath);
        command.Parameters.AddWithValue("$sourceSizeBytes", sourceSizeBytes);
        command.Parameters.AddWithValue("$sourceLastWriteUtcTicks", sourceLastWriteUtcTicks);
        command.Parameters.AddWithValue("$cacheSchemaVersion", CacheSchemaVersion);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MediaInfo(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            NullableDouble(reader, 5),
            NullableLong(reader, 6),
            NullableInt(reader, 7),
            NullableInt(reader, 8),
            NullableLong(reader, 9));
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
            Mode = SqliteOpenMode.ReadWriteCreate,
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
