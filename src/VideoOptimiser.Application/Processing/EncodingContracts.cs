using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Processing;

public sealed record EncodeResult(string OutputPath, TimeSpan Duration);

public interface IVideoEncoder
{
    /// <param name="flacAudioStreams">Audio-relative indexes (the N of <c>0:a:N</c>) of the streams to re-encode to FLAC; the rest are copied.</param>
    IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams);
    Task<EncodeResult> EncodeAsync(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
}
