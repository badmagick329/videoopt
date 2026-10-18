namespace VideoOptimiser.Application.Tools;

public sealed record MediaInfo(
    string PrimaryVideoCodec,
    int VideoStreamCount,
    int AudioStreamCount,
    int SubtitleStreamCount,
    int AttachmentCount,
    double? DurationSeconds,
    long? SizeBytes,
    int? PrimaryVideoWidth = null,
    int? PrimaryVideoHeight = null,
    long? PrimaryVideoBitrate = null,
    IReadOnlyList<AudioTrack>? AudioTracks = null);

/// <summary>One audio stream, in audio-stream order. <see cref="Tag"/> is the container's codec tag, which names a codec
/// FFmpeg cannot identify (APAC is <c>apac</c>) when <see cref="Codec"/> is <c>unknown</c>.</summary>
public sealed record AudioTrack(string Codec, string? Tag = null);

public sealed record MediaTimeline(
    double FirstVideoTimestampSeconds,
    double LastVideoEndTimestampSeconds,
    double? FirstPrimaryAudioTimestampSeconds)
{
    public double PrimaryVideoDurationSeconds => LastVideoEndTimestampSeconds - FirstVideoTimestampSeconds;
}

public sealed record MediaProbeCacheEntry(
    string SourcePath,
    long SourceSizeBytes,
    long SourceLastWriteUtcTicks,
    MediaInfo MediaInfo);

public interface IMediaProbe
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

public interface IMediaTimelineProbe
{
    Task<MediaTimeline> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// ffprobe could not read a media file: it exited non-zero, or reported nothing usable. A dedicated type, rather than
/// <see cref="InvalidDataException"/>, because the CLI reads that one as an unreadable configuration file.
/// </summary>
public sealed class MediaProbeException(string message, Exception? innerException = null) : Exception(message, innerException);

public interface IMediaProbeCache
{
    Task<IReadOnlyList<MediaProbeCacheEntry>> LoadAllAsync(
        string databasePath,
        CancellationToken cancellationToken = default);

    Task StoreAsync(
        string databasePath,
        MediaProbeCacheEntry entry,
        CancellationToken cancellationToken = default);
}
