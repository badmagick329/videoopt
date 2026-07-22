using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Scanning;

public static class EligibilityEvaluator
{
    public static bool IsEligible(FileInfo file, MediaInfo media, EligibilitySettings settings, out string reason)
    {
        for (var index = 0; index < settings.Rules.Count; index++)
        {
            if (Matches(settings.Rules[index], file, media))
            {
                reason = $"Matches eligibility rule {index + 1}.";
                return true;
            }
        }

        reason = string.Join(" ", settings.Rules.Select((rule, index) => $"Rule {index + 1}: {DescribeFailures(rule, file, media)}"));
        return false;
    }

    public static bool IsValidResolutionBand(string? value) => value?.ToLowerInvariant() is "1080p-1440p" or "1440p-4k" or "4k+";

    private static bool Matches(EligibilityRuleSettings rule, FileInfo file, MediaInfo media) =>
        rule.Codecs.Contains(media.PrimaryVideoCodec, StringComparer.OrdinalIgnoreCase) &&
        HumanReadableValues.TryParseSize(rule.MinimumFileSize, out var minimumSize) && file.Length >= minimumSize &&
        HumanReadableValues.TryParseBitrate(rule.MinimumVideoBitrate, out var minimumBitrate) && media.PrimaryVideoBitrate >= minimumBitrate &&
        ResolutionMatches(rule.Resolution, media.PrimaryVideoWidth, media.PrimaryVideoHeight);

    private static string DescribeFailures(EligibilityRuleSettings rule, FileInfo file, MediaInfo media)
    {
        var failures = new List<string>();
        if (!rule.Codecs.Contains(media.PrimaryVideoCodec, StringComparer.OrdinalIgnoreCase)) failures.Add($"codec is {media.PrimaryVideoCodec}, needs {string.Join("/", rule.Codecs)}");
        if (!HumanReadableValues.TryParseSize(rule.MinimumFileSize, out var minimumSize) || file.Length < minimumSize) failures.Add($"size is below {rule.MinimumFileSize}");
        if (!HumanReadableValues.TryParseBitrate(rule.MinimumVideoBitrate, out var minimumBitrate) || media.PrimaryVideoBitrate is null) failures.Add($"video bitrate is unavailable, needs {rule.MinimumVideoBitrate}");
        else if (media.PrimaryVideoBitrate < minimumBitrate) failures.Add($"video bitrate is below {rule.MinimumVideoBitrate}");
        if (!ResolutionMatches(rule.Resolution, media.PrimaryVideoWidth, media.PrimaryVideoHeight)) failures.Add($"resolution is outside {rule.Resolution}");
        return failures.Count == 0 ? "did not match." : string.Join("; ", failures) + ".";
    }

    private static bool ResolutionMatches(string value, int? width, int? height)
    {
        if (width is null || height is null) return false;
        var pixels = (long)width.Value * height.Value;
        var (minimum, maximum) = value.ToLowerInvariant() switch
        {
            "1080p-1440p" => (1_920L * 1_080, 2_560L * 1_440),
            "1440p-4k" => (2_560L * 1_440, 3_840L * 2_160),
            "4k+" => (3_840L * 2_160, long.MaxValue),
            _ => (long.MaxValue, 0)
        };
        return pixels >= minimum && pixels < maximum;
    }
}
