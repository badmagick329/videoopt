using VideoOptimiser.Application.Scanning;

namespace VideoOptimiser.Application.Jobs;

/// <summary>
/// What a probe said about a source when its job was created or its Immich asset skipped: the primary video stream's
/// codec, size and bitrate, and the source's duration and size. Kept on the row because the probe cache is keyed by path,
/// and an Immich job's path is a staged copy that cleanup deletes; <c>calibrate</c> judges the eligibility rules by them.
/// </summary>
public sealed record VideoFacts(string Codec, int? Width, int? Height, long? Bitrate, double? DurationSeconds, long SizeBytes)
{
    /// <param name="sizeBytes">The size of the bytes that were probed; <see cref="MediaInfo.SizeBytes"/> is ffprobe's own
    /// reading of the container, which may be missing.</param>
    public static VideoFacts From(MediaInfo media, long sizeBytes) =>
        new(media.PrimaryVideoCodec, media.PrimaryVideoWidth, media.PrimaryVideoHeight, media.PrimaryVideoBitrate, media.DurationSeconds, sizeBytes);
}
