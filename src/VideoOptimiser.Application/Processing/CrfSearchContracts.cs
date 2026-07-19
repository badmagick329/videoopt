using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Processing;

public sealed record CrfSearchResult(int Crf, string StandardOutput, string StandardError, TimeSpan Duration);
public sealed record CrfSearchOutput(string Stream, string Text);

public interface ICrfSearchClient
{
    Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default);
    IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings);
}
