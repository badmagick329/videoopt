using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Scanning;

/// <param name="Text">The short reason, with the file's real values.</param>
/// <param name="Key">The same reason without them, to count alike skips together.</param>
public sealed record EligibilityMismatch(string Text, string Key);

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

    /// <summary>
    /// Why a file that <see cref="IsEligible"/> rejected was rejected, short enough for one output line and with the real
    /// values (<c>bitrate 6.5Mbps below 10Mbps</c>). It describes the closest rule, the one with the codec and the fewest
    /// failures, because listing every rule's failures would bury the one the user can act on. <see cref="EligibilityMismatch.Key"/>
    /// drops the file's values, so skips that failed alike can be counted together. <see cref="IsEligible"/> keeps its long
    /// reason for file mode, where it ends up in a failed job's message.
    /// </summary>
    public static EligibilityMismatch DescribeMismatch(long fileSizeBytes, MediaInfo media, EligibilitySettings settings)
    {
        var rules = settings.Rules;
        if (rules.Count == 0) return new EligibilityMismatch("no eligibility rules are configured", "no eligibility rules");

        var withCodec = rules
            .Select((rule, index) => (Rule: rule, Number: index + 1))
            .Where(candidate => candidate.Rule.Codecs.Contains(media.PrimaryVideoCodec, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (withCodec.Length == 0)
        {
            var wanted = string.Join("/", rules.SelectMany(rule => rule.Codecs).Distinct(StringComparer.OrdinalIgnoreCase));
            return new EligibilityMismatch($"codec {media.PrimaryVideoCodec} (rules want {wanted})", $"codec {media.PrimaryVideoCodec}");
        }

        var closest = withCodec
            .Select(candidate => (candidate.Number, Failures: ShortFailures(candidate.Rule, fileSizeBytes, media)))
            .MinBy(candidate => candidate.Failures.Count);
        if (closest.Failures.Count == 0) return new EligibilityMismatch("did not match", "did not match");

        var suffix = rules.Count > 1 ? $" (rule {closest.Number})" : string.Empty;
        return new EligibilityMismatch(
            string.Join("; ", closest.Failures.Select(failure => failure.Text)) + suffix,
            string.Join("; ", closest.Failures.Select(failure => failure.Key)) + suffix);
    }

    public static bool IsValidResolutionBand(string? value) => value?.ToLowerInvariant() is "1080p-1440p" or "1440p-4k" or "4k+";

    /// <summary>
    /// Identifies what <see cref="IsEligible"/> would decide for any file, so a verdict stored with it goes stale once the
    /// rules change. Rule order, codec order, case and size or bitrate spelling do not change a verdict, so they do not
    /// change the fingerprint either.
    /// </summary>
    public static string RulesFingerprint(EligibilitySettings settings)
    {
        var rules = settings.Rules
            .Select(rule => string.Join('|',
                string.Join(',', rule.Codecs.Select(codec => codec.ToLowerInvariant()).Order(StringComparer.Ordinal)),
                rule.Resolution.ToLowerInvariant(),
                HumanReadableValues.TryParseBitrate(rule.MinimumVideoBitrate, out var bitrate) ? bitrate.ToString(System.Globalization.CultureInfo.InvariantCulture) : rule.MinimumVideoBitrate,
                HumanReadableValues.TryParseSize(rule.MinimumFileSize, out var size) ? size.ToString(System.Globalization.CultureInfo.InvariantCulture) : rule.MinimumFileSize))
            .Order(StringComparer.Ordinal);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', rules)));
        return Convert.ToHexStringLower(hash)[..16];
    }

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

    private static List<(string Text, string Key)> ShortFailures(EligibilityRuleSettings rule, long fileSizeBytes, MediaInfo media)
    {
        var failures = new List<(string Text, string Key)>();
        if (media.PrimaryVideoBitrate is not { } bitrate) failures.Add(($"bitrate unknown (needs {rule.MinimumVideoBitrate})", "bitrate unknown"));
        else if (!HumanReadableValues.TryParseBitrate(rule.MinimumVideoBitrate, out var minimumBitrate) || bitrate < minimumBitrate) failures.Add(($"bitrate {FormatBitrate(bitrate)} below {rule.MinimumVideoBitrate}", $"bitrate below {rule.MinimumVideoBitrate}"));

        if (!ResolutionMatches(rule.Resolution, media.PrimaryVideoWidth, media.PrimaryVideoHeight))
        {
            var actual = media.PrimaryVideoWidth is { } width && media.PrimaryVideoHeight is { } height ? $"{width}x{height}" : "unknown";
            failures.Add(($"resolution {actual} outside {rule.Resolution}", $"resolution outside {rule.Resolution}"));
        }

        if (!HumanReadableValues.TryParseSize(rule.MinimumFileSize, out var minimumSize) || fileSizeBytes < minimumSize)
            failures.Add(($"size {HumanReadableValues.FormatSize(fileSizeBytes)} below {rule.MinimumFileSize}", $"size below {rule.MinimumFileSize}"));
        return failures;
    }

    // The unit the rules are written in (Mbps), so the value reads against the threshold it failed.
    private static string FormatBitrate(long bitsPerSecond) => bitsPerSecond >= 1_000_000
        ? $"{(bitsPerSecond / 1_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}Mbps"
        : $"{(bitsPerSecond / 1_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}Kbps";

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
