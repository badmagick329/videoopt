using System.Globalization;
using System.Text.RegularExpressions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.Infrastructure.Processing;

/// <summary>
/// Reads a failed ab-av1 run's log. Local and remote runs both end up here, so one reading decides what counts as "AV1 would
/// not save enough" and what the error message says. ab-av1 writes its INFO logs to stderr beside the error, so the first
/// stderr line says nothing about why the run failed.
/// </summary>
public static partial class AbAv1Output
{
    private const string NoSuitableCrf = "Failed to find a suitable crf";

    // Only the crf_search summary lines end in "(P%)"; sample_encode lines for the same CRF continue with "predicted ...".
    [GeneratedRegex(@"\bcrf\s+(?<crf>\d+)\s+VMAF\s+(?<vmaf>\d+(?:\.\d+)?)\s+\((?<percent>\d+(?:\.\d+)?)%\)")]
    private static partial Regex AttemptLine();

    /// <summary>
    /// The exception for a run that found no CRF meeting both the VMAF and the size limit, or null for any other failure.
    /// Its best attempt is the smallest one that met the minimum VMAF; ab-av1 itself only fails when none was small enough.
    /// </summary>
    public static NoWorthwhileCrfException? NoWorthwhileCrf(string log, QualitySettings quality)
    {
        if (!log.Contains(NoSuitableCrf, StringComparison.Ordinal)) return null;
        var best = AttemptLine().Matches(log)
            .Select(match => new CrfAttempt(
                int.Parse(match.Groups["crf"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["vmaf"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["percent"].Value, CultureInfo.InvariantCulture)))
            .Where(attempt => attempt.Vmaf >= quality.MinimumVmaf)
            .MinBy(attempt => attempt.EncodedPercent);
        return new NoWorthwhileCrfException(best, quality);
    }

    /// <summary>The last <c>Error:</c> line without its prefix, or null when the log has none.</summary>
    public static string? LastErrorLine(string log) => Lines(log)
        .Where(line => line.StartsWith("Error:", StringComparison.Ordinal))
        .Select(line => line["Error:".Length..].Trim())
        .LastOrDefault(line => line.Length > 0);

    /// <summary>The last <c>Error:</c> line, else the last non-empty line.</summary>
    public static string Summarise(string log) => LastErrorLine(log) ?? Lines(log).LastOrDefault() ?? "No error output.";

    private static string[] Lines(string log) =>
        log.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
