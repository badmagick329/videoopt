using System.Globalization;
using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Processing;

/// <summary>
/// ab-av1's estimate for the chosen CRF, kept as it printed it: <see cref="Size"/> is its own text (<c>284.87 MiB</c>) and
/// <see cref="Taking"/> how long the encode should take (<c>5 minutes</c>). Only a search that ran in this process has one;
/// a job resumed with a stored CRF does not.
/// </summary>
public sealed record CrfPrediction(string Size, int Percent, string Taking);

public sealed record CrfSearchResult(int Crf, string StandardOutput, string StandardError, TimeSpan Duration, CrfPrediction? Prediction = null);

/// <summary>
/// An ab-av1 stage failed for a reason other than "not worth converting". <see cref="LogPath"/> is the kept copy of its full
/// log, when one could be written, so the job's failure can point at it instead of at a one-line summary.
/// </summary>
public sealed class AbAv1StageException(string message, string? logPath, Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public string? LogPath { get; } = logPath;
}

public interface ICrfSearchClient
{
    Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
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
