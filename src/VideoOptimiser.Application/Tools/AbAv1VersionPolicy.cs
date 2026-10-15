using System.Globalization;

namespace VideoOptimiser.Application.Tools;

/// <summary>
/// One ab-av1 version rule for local and remote doctor checks, so both modes reject the same too-old install.
/// </summary>
public static class AbAv1VersionPolicy
{
    private const string OutputPrefix = "ab-av1 ";

    // The version the bootstrap pins and CI tests, so a job runs the ab-av1 code the tests exercised.
    public static readonly Version Minimum = new(0, 11, 7);

    /// <summary>
    /// Accepts a bare version (<c>0.11.7</c>, <c>v0.11.7</c>) as the remote probe extracts it, and the raw
    /// <c>--version</c> line (<c>ab-av1 0.11.7</c>) that local doctor already holds.
    /// </summary>
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith(OutputPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[OutputPrefix.Length..].TrimStart();
        }

        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var components = normalized.Split('.', StringSplitOptions.None);
        if (components.Length != 3 || components.Any(component => !int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        version = new Version(
            int.Parse(components[0], CultureInfo.InvariantCulture),
            int.Parse(components[1], CultureInfo.InvariantCulture),
            int.Parse(components[2], CultureInfo.InvariantCulture));
        return true;
    }
}
