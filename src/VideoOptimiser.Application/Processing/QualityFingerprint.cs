using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Processing;

/// <summary>
/// Identifies the settings that decide whether AV1 is worth it for a video. A job skipped as not worth converting stores
/// it, and discovery treats that skip as settled only while the fingerprint still matches, so loosening the quality bar
/// checks the video again. Sample count and duration are left out: they change how the search measures, not what it demands.
/// <c>savings.minimumBytesSaved</c> is in because it sets the size cap; it counts as parsed bytes, so respelling it
/// (<c>100MiB</c> as <c>100 MiB</c>) does not unsettle every skip.
/// </summary>
public static class QualityFingerprint
{
    public static string Compute(AppSettings settings)
    {
        var quality = settings.Quality;
        if (!HumanReadableValues.TryParseSize(settings.Savings.MinimumBytesSaved, out var minimumBytesSaved))
            throw new InvalidOperationException("savings.minimumBytesSaved must be validated before the quality fingerprint is computed.");
        var text = string.Join('|',
            quality.Encoder,
            quality.Preset.ToString(CultureInfo.InvariantCulture),
            quality.PixelFormat,
            quality.MinimumVmaf.ToString("R", CultureInfo.InvariantCulture),
            quality.MaxEncodedPercent.ToString(CultureInfo.InvariantCulture),
            quality.CrfSearch.MinCrf.ToString(CultureInfo.InvariantCulture),
            quality.CrfSearch.MaxCrf.ToString(CultureInfo.InvariantCulture),
            minimumBytesSaved.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
}
