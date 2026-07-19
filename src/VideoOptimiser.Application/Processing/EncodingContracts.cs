using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Processing;

public sealed record EncodeResult(string OutputPath, TimeSpan Duration);

public interface IVideoEncoder
{
    IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int crf, QualitySettings settings);
    Task<EncodeResult> EncodeAsync(string inputPath, string outputPath, int crf, QualitySettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default);
}
