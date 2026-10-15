using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Eligibility;

public static class EligibilityEvaluator
{
    public static bool IsEligible(FileInfo file, MediaInfo media, EligibilitySettings settings, out string reason)
    {
        if (MatchingRule(file.Length, media, settings) is { } rule)
        {
            reason = $"Matches eligibility rule {rule}.";
            return true;
        }

        reason = string.Join(" ", settings.Rules.Select((rule, index) => $"Rule {index + 1}: {DescribeFailures(rule, file, media)}"));
        return false;
    }

    /// <summary>
    /// The number (1-based, in config order) of the first rule the file matches, or null when none does. The number is what
    /// the user is told, because a file that fits rule 3 while another of the same size fails rule 1 is otherwise a puzzle.
    /// </summary>
    public static int? MatchingRule(long fileSizeBytes, MediaInfo media, EligibilitySettings settings)
    {
        for (var index = 0; index < settings.Rules.Count; index++)
        {
            if (Matches(settings.Rules[index], fileSizeBytes, media)) return index + 1;
        }

        return null;
    }

    /// <summary>
    /// Why a file that <see cref="IsEligible"/> rejected was rejected, short enough for one output line: what the closest rule
    /// needs, written as in the config (<c>rule 2 needs 10Mbps</c>), not the file's own values, which the caller shows
    /// beside it. The closest rule is the one with the codec and the fewest failures, because listing every rule's failures
    /// would bury the one the user can act on. The text holds nothing specific to the file, so it is also the key that counts
    /// alike skips together. <see cref="IsEligible"/> keeps its long reason for file mode, where it ends up in a failed job's
    /// message.
    /// </summary>
    public static string DescribeMismatch(long fileSizeBytes, MediaInfo media, EligibilitySettings settings)
    {
        var rules = settings.Rules;
        if (rules.Count == 0) return "no eligibility rules are configured";

        var withCodec = rules
            .Select((rule, index) => (Rule: rule, Number: index + 1))
            .Where(candidate => candidate.Rule.Codecs.Contains(media.PrimaryVideoCodec, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (withCodec.Length == 0) return $"no rule for {media.PrimaryVideoCodec}";

        var closest = withCodec
            .Select(candidate => (candidate.Number, Needs: Needs(candidate.Rule, fileSizeBytes, media)))
            .MinBy(candidate => candidate.Needs.Count);
        if (closest.Needs.Count == 0) return "did not match";

        var needs = $"needs {string.Join(" and ", closest.Needs)}";
        // With one rule there is nothing to tell apart, so its number would only be noise.
        return rules.Count > 1 ? $"rule {closest.Number} {needs}" : needs;
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

    private static bool Matches(EligibilityRuleSettings rule, long fileSizeBytes, MediaInfo media) =>
        rule.Codecs.Contains(media.PrimaryVideoCodec, StringComparer.OrdinalIgnoreCase) &&
        HumanReadableValues.TryParseSize(rule.MinimumFileSize, out var minimumSize) && fileSizeBytes >= minimumSize &&
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

    // What the rule asks for, per failed condition and as the config spells it: the file's own values are shown beside the reason.
    private static List<string> Needs(EligibilityRuleSettings rule, long fileSizeBytes, MediaInfo media)
    {
        var needs = new List<string>();
        if (!HumanReadableValues.TryParseSize(rule.MinimumFileSize, out var minimumSize) || fileSizeBytes < minimumSize) needs.Add(rule.MinimumFileSize);
        if (media.PrimaryVideoBitrate is not { } bitrate) needs.Add("a known bitrate");
        else if (!HumanReadableValues.TryParseBitrate(rule.MinimumVideoBitrate, out var minimumBitrate) || bitrate < minimumBitrate) needs.Add(rule.MinimumVideoBitrate);
        if (!ResolutionMatches(rule.Resolution, media.PrimaryVideoWidth, media.PrimaryVideoHeight)) needs.Add(rule.Resolution);
        return needs;
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
