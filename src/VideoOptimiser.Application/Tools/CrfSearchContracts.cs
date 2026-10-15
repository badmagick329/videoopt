using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Tools;

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
/// ab-av1 found no CRF that meets both the minimum VMAF and the video's <see cref="SizeCap"/>. That is an expected outcome
/// (AV1 would not shrink this video enough), not a failure, and a retry would end the same way. <see cref="Best"/> is the
/// smallest attempt that met the VMAF, or null when none did.
/// </summary>
public sealed class NoWorthwhileCrfException : Exception
{
    public NoWorthwhileCrfException(IReadOnlyList<CrfAttempt> attempts, QualitySettings quality) : this(Analyse(attempts, quality), quality)
    {
    }

    private NoWorthwhileCrfException((CrfAttempt? Best, CrfAttempt? AtMinCrf) found, QualitySettings quality) : base(Describe(found.Best, found.AtMinCrf, quality))
    {
        Best = found.Best;
    }

    public CrfAttempt? Best { get; }

    private static (CrfAttempt? Best, CrfAttempt? AtMinCrf) Analyse(IReadOnlyList<CrfAttempt> attempts, QualitySettings quality) => (
        attempts.Where(attempt => attempt.Vmaf >= quality.MinimumVmaf).MinBy(attempt => attempt.EncodedPercent),
        attempts.FirstOrDefault(attempt => attempt.Crf == quality.CrfSearch.MinCrf));

    // With no quality-passing attempt, ab-av1 may have stopped early: a lower CRF raises the VMAF but also the size, so once
    // an attempt is already over the size limit it tries no lower CRF, and naming both limits is all that stays true. When it
    // reached the lowest allowed CRF and that still missed the VMAF, size played no part, and naming quality.maxEncodedPercent
    // would send the user to a setting that cannot help. When savings.minimumBytesSaved set the cap, the reason names it in
    // bytes: a percentage of the video stream would not match any size the user set or sees.
    private static string Describe(CrfAttempt? best, CrfAttempt? atMinCrf, QualitySettings quality)
    {
        var vmaf = quality.MinimumVmaf.ToString("0.##", CultureInfo.InvariantCulture);
        var bytesCap = quality.SizeCap is { SetByMinimumBytes: true } cap ? cap : null;
        if (best is not null)
        {
            var setting = $"(CRF {best.Crf}, VMAF {best.Vmaf.ToString("F1", CultureInfo.InvariantCulture)})";
            return bytesCap is null
                ? $"AV1 would make it {best.EncodedPercent.ToString("0.#", CultureInfo.InvariantCulture)}% of the original's size at the best quality-passing setting {setting}; the limit is {quality.MaxEncodedPercent}% (quality.maxEncodedPercent)."
                : $"AV1 would save about {HumanReadableValues.FormatSize(bytesCap.BytesSaved(best.EncodedPercent))} at the best quality-passing setting {setting}; the minimum is {bytesCap.MinimumText} (savings.minimumBytesSaved).";
        }
        if (atMinCrf is not null)
            return $"No CRF reached VMAF {vmaf}; even CRF {atMinCrf.Crf}, the lowest allowed (quality.crfSearch.minCrf), scored {atMinCrf.Vmaf.ToString("F1", CultureInfo.InvariantCulture)}.";
        return bytesCap is null
            ? $"No CRF reached VMAF {vmaf} within {quality.MaxEncodedPercent}% of the original's size (quality.maxEncodedPercent)."
            : $"No CRF reached VMAF {vmaf} while saving {bytesCap.MinimumText} (savings.minimumBytesSaved).";
    }
}
