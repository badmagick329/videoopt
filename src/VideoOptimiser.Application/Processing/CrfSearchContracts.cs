using System.Globalization;
using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Processing;

public sealed record CrfSearchResult(int Crf, string StandardOutput, string StandardError, TimeSpan Duration);
public sealed record CrfSearchOutput(string Stream, string Text);

public interface ICrfSearchClient
{
    Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default);
    IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings);
}

/// <summary>One CRF ab-av1 tried: the VMAF it reached and the share of the original's size the encode would keep.</summary>
public sealed record CrfAttempt(int Crf, double Vmaf, double EncodedPercent);

/// <summary>
/// ab-av1 found no CRF that meets both the minimum VMAF and <c>quality.maxEncodedPercent</c>. That is an expected outcome
/// (AV1 would not shrink this video enough), not a failure, and a retry would end the same way. <see cref="Best"/> is the
/// smallest attempt that met the VMAF, or null when none did.
/// </summary>
public sealed class NoWorthwhileCrfException(CrfAttempt? best, QualitySettings quality) : Exception(Describe(best, quality))
{
    public CrfAttempt? Best { get; } = best;

    private static string Describe(CrfAttempt? best, QualitySettings quality) => best is null
        ? $"No CRF from {quality.CrfSearch.MinCrf} to {quality.CrfSearch.MaxCrf} reached VMAF {quality.MinimumVmaf.ToString("0.##", CultureInfo.InvariantCulture)}."
        : $"AV1 would keep {best.EncodedPercent.ToString("0.#", CultureInfo.InvariantCulture)}% of the size at the best quality-passing setting (CRF {best.Crf}, VMAF {best.Vmaf.ToString("F1", CultureInfo.InvariantCulture)}); the limit is {quality.MaxEncodedPercent}% (quality.maxEncodedPercent).";
}
