using System.Globalization;
using System.Text.RegularExpressions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.Infrastructure.Tools;

/// <summary>
/// Reads a failed ab-av1 run's log. Local and remote runs both end up here, so one reading decides what counts as "AV1 would
/// not save enough" and what the error message says. ab-av1 writes its INFO logs to stderr beside the error, so the first
/// stderr line says nothing about why the run failed.
/// </summary>
public static partial class AbAv1Output
{
    private const string NoSuitableCrf = "Failed to find a suitable crf";

    // Only crf_search's line is an attempt's verdict. sample_encode's per-sample lines ("sample 2/2 crf 32 VMAF 97.39 (82%)")
    // share its shape, and one sample can pass the VMAF while the attempt as a whole does not.
    [GeneratedRegex(@"::crf_search\]\s+crf\s+(?<crf>\d+)\s+VMAF\s+(?<vmaf>\d+(?:\.\d+)?)\s+\((?<percent>\d+(?:\.\d+)?)%\)")]
    private static partial Regex AttemptLine();

    /// <summary>
    /// The exception for a run that found no CRF meeting both the VMAF and the size limit, or null for any other failure.
    /// </summary>
    public static NoWorthwhileCrfException? NoWorthwhileCrf(string log, QualitySettings quality)
    {
        if (!log.Contains(NoSuitableCrf, StringComparison.Ordinal)) return null;
        var attempts = AttemptLine().Matches(log)
            .Select(match => new CrfAttempt(
                int.Parse(match.Groups["crf"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["vmaf"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["percent"].Value, CultureInfo.InvariantCulture)))
            .ToList();
        return new NoWorthwhileCrfException(attempts, quality);
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
