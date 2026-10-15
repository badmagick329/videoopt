using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Jobs;

public static class VideoFactsReading
{
    /// <param name="sizeBytes">The size of the bytes that were probed; <see cref="MediaInfo.SizeBytes"/> is ffprobe's own
    /// reading of the container, which may be missing.</param>
    public static VideoFacts ToVideoFacts(this MediaInfo media, long sizeBytes) =>
        new(media.PrimaryVideoCodec, media.PrimaryVideoWidth, media.PrimaryVideoHeight, media.PrimaryVideoBitrate, media.DurationSeconds, sizeBytes);
}
