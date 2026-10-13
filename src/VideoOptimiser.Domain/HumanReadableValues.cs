using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoOptimiser.Domain;

public static partial class HumanReadableValues
{
    [GeneratedRegex("^(?<value>\\d+(?:\\.\\d+)?)\\s*(?<unit>B|KB|MB|GB|TB|KiB|MiB|GiB|TiB)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();

    [GeneratedRegex("^(?<value>\\d+(?:\\.\\d+)?)\\s*(?<unit>ms|s|m|h|d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    [GeneratedRegex("^(?<value>\\d+(?:\\.\\d+)?)\\s*(?<unit>Kbps|Mbps|Gbps)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BitratePattern();

    public static string FormatSize(long bytes)
    {
        const double kilobyte = 1024;
        const double megabyte = kilobyte * 1024;
        const double gigabyte = megabyte * 1024;
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < (long)megabyte => $"{bytes / kilobyte:0.0} KiB",
            < (long)gigabyte => $"{bytes / megabyte:0.0} MiB",
            _ => $"{bytes / gigabyte:0.0} GiB"
        };
    }

    /// <summary>
    /// A size a tool wrote as text (ab-av1's <c>208.92 MiB</c>) in the format every command prints (<c>208.9 MiB</c>), so the
    /// same size never shows two spellings in one block. Text that is not a size is returned as it came: the tool's own
    /// words beat a guess.
    /// </summary>
    public static string FormatSizeText(string text) => TryParseSize(text, out var bytes) ? FormatSize(bytes) : text;

    /// <summary>The one duration format every command prints: <c>45s</c>, <c>7m 36s</c>, <c>1h 05m</c>.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m"
            : duration.TotalMinutes >= 1 ? $"{duration.Minutes}m {duration.Seconds:00}s"
            : $"{duration.Seconds}s";
    }

    public static bool TryParseBitrate(string? value, out long bitsPerSecond)
    {
        bitsPerSecond = 0;
        var match = string.IsNullOrWhiteSpace(value) ? Match.Empty : BitratePattern().Match(value.Trim());
        if (!match.Success || !decimal.TryParse(match.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) || number <= 0) return false;
        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch { "KBPS" => 1_000m, "MBPS" => 1_000_000m, "GBPS" => 1_000_000_000m, _ => 0m };
        var result = number * multiplier;
        if (result > long.MaxValue) return false;
        bitsPerSecond = decimal.ToInt64(result);
        return true;
    }

    public static bool TryParseSize(string? value, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = SizePattern().Match(value.Trim());
        if (!match.Success || !decimal.TryParse(match.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var numericValue))
        {
            return false;
        }

        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "B" => 1m,
            "KB" => 1_000m,
            "MB" => 1_000_000m,
            "GB" => 1_000_000_000m,
            "TB" => 1_000_000_000_000m,
            "KIB" => 1_024m,
            "MIB" => 1_048_576m,
            "GIB" => 1_073_741_824m,
            "TIB" => 1_099_511_627_776m,
            _ => 0m
        };

        var result = numericValue * multiplier;
        if (result < 0m || result > long.MaxValue)
        {
            return false;
        }

        bytes = decimal.ToInt64(decimal.Truncate(result));
        return true;
    }

    public static bool TryParseDuration(string? value, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = DurationPattern().Match(value.Trim());
        if (!match.Success || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue) || numericValue < 0d)
        {
            return false;
        }

        try
        {
            duration = match.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "ms" => TimeSpan.FromMilliseconds(numericValue),
                "s" => TimeSpan.FromSeconds(numericValue),
                "m" => TimeSpan.FromMinutes(numericValue),
                "h" => TimeSpan.FromHours(numericValue),
                "d" => TimeSpan.FromDays(numericValue),
                _ => default
            };
        }
        catch (OverflowException)
        {
            return false;
        }

        return duration >= TimeSpan.Zero;
    }
}
