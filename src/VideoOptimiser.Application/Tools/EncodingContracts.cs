using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Application.Tools;

public sealed record EncodeResult(string OutputPath, TimeSpan Duration);

public interface IVideoEncoder
{
    /// <param name="audio">What to do with each audio stream: dropped tracks are left out of the output, PCM ones re-encoded to FLAC, the rest copied.</param>
    IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int crf, QualitySettings settings, AudioPlan audio);
    Task<EncodeResult> EncodeAsync(string inputPath, string outputPath, int crf, QualitySettings settings, AudioPlan audio, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
}
