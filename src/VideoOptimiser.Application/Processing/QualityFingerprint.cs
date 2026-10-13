using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Processing;

/// <summary>
/// Identifies the settings that decide whether AV1 is worth it for a video. A job skipped as not worth converting stores
/// it, and discovery treats that skip as settled only while the fingerprint still matches, so loosening the quality bar
/// checks the video again. Sample count and duration are left out: they change how the search measures, not what it demands.
/// </summary>
public static class QualityFingerprint
{
    public static string Compute(QualitySettings quality)
    {
        var text = string.Join('|',
            quality.Encoder,
            quality.Preset.ToString(CultureInfo.InvariantCulture),
            quality.PixelFormat,
            quality.MinimumVmaf.ToString("R", CultureInfo.InvariantCulture),
            quality.MaxEncodedPercent.ToString(CultureInfo.InvariantCulture),
            quality.CrfSearch.MinCrf.ToString(CultureInfo.InvariantCulture),
            quality.CrfSearch.MaxCrf.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
}
