using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.Infrastructure.Tools;

public sealed class AbAv1CrfSearchClient(string executablePath) : ICrfSearchClient
{
    public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) =>
    [
        "crf-search", "--input", inputPath,
        "--encoder", settings.Encoder,
        "--pix-format", settings.PixelFormat,
        "--preset", settings.Preset.ToString(CultureInfo.InvariantCulture),
        "--min-vmaf", settings.MinimumVmaf.ToString(CultureInfo.InvariantCulture),
        // Passed explicitly so the setting, not ab-av1's default, decides when a video is not worth converting.
        "--max-encoded-percent", settings.MaxEncodedPercent.ToString(CultureInfo.InvariantCulture),
        "--min-crf", settings.CrfSearch.MinCrf.ToString(CultureInfo.InvariantCulture),
        "--max-crf", settings.CrfSearch.MaxCrf.ToString(CultureInfo.InvariantCulture),
        // CRFs are whole numbers throughout (jobs, manifests, encode). ab-av1 0.11 defaults svt-av1 to quarter steps.
        "--crf-increment", "1",
        "--samples", settings.CrfSearch.SampleCount.ToString(CultureInfo.InvariantCulture),
        "--sample-duration", settings.CrfSearch.SampleDuration,
        // The same frames as the final encode (see AbAv1VideoEncoder), so each sample's VMAF and size predict it.
        "--enc", "fps_mode=passthrough"
    ];

    public async Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        // Each list is filled by its own stream's reader only, so neither needs a lock.
        List<string> outputLines = [], errorLines = [];
        var exitCode = await ChildProcess.StreamAsync(
            new ExternalProcessRequest(executablePath, BuildArguments(inputPath, settings)),
            line => { outputLines.Add(line); progress?.Report(JobProgress.Tool(line)); },
            line => { errorLines.Add(line); progress?.Report(JobProgress.Tool(line)); },
            cancellationToken);
        var output = string.Join(Environment.NewLine, outputLines);
        var error = string.Join(Environment.NewLine, errorLines);
        started.Stop();
        if (exitCode != 0)
        {
            var log = output + Environment.NewLine + error;
            // Not a failure: no CRF both met the VMAF and shrank the video enough, and a retry would end the same way.
            if (AbAv1Output.NoWorthwhileCrf(log, settings) is { } noWorthwhileCrf) throw noWorthwhileCrf;
            throw new InvalidOperationException($"ab-av1 crf-search failed with exit code {exitCode}: {AbAv1Output.Summarise(error)}");
        }

        var combined = output + Environment.NewLine + error;
        var crf = CrfSearchOutputParser.Parse(combined);
        return new CrfSearchResult(crf, output, error, started.Elapsed, CrfSearchOutputParser.TryParsePrediction(combined, crf, out var prediction) ? prediction : null);
    }
}

public static partial class CrfSearchOutputParser
{
    [GeneratedRegex(@"(?im)^\s*(?:best\s+)?crf(?:\s+value)?\s*[:=]\s*(?<crf>\d+(?:\.0+)?)\s*$")]
    private static partial Regex CrfLine();

    [GeneratedRegex(@"(?im)\bcrf\s+(?<crf>\d+(?:\.0+)?)\s+successful\s*$")]
    private static partial Regex SuccessfulCrfLine();

    // ab-av1 prints the chosen CRF's estimate as a sample_encode INFO line and again, bare, on stdout. A rerun marks lines
    // that reused earlier samples "(cache)".
    [GeneratedRegex(@"\bcrf\s+(?<crf>\d+)(?:\.\d+)?\s+VMAF\s+\d+(?:\.\d+)?\s+predicted video stream size\s+(?<size>\d+(?:\.\d+)?\s+\w+)\s+\((?<percent>\d+)%\)\s+taking\s+(?<taking>.+?)(?:\s+\(cache\))?\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PredictionLine();

    /// <summary>Reads ab-av1's size and time estimate for <paramref name="crf"/>; false when its output has none.</summary>
    public static bool TryParsePrediction(string output, int crf, out CrfPrediction? prediction)
    {
        var match = PredictionLine().Matches(output)
            .LastOrDefault(candidate => string.Equals(candidate.Groups["crf"].Value, crf.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        prediction = match is null ? null : new CrfPrediction(match.Groups["size"].Value, int.Parse(match.Groups["percent"].Value, CultureInfo.InvariantCulture), match.Groups["taking"].Value);
        return prediction is not null;
    }

    public static int Parse(string output)
    {
        var values = CrfLine().Matches(output).Concat(SuccessfulCrfLine().Matches(output))
            .Select(match => match.Groups["crf"].Value.Split('.')[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (values.Length != 1 || !int.TryParse(values[0], CultureInfo.InvariantCulture, out var crf))
        {
            throw new InvalidDataException("ab-av1 completed but its selected CRF could not be parsed safely. Raw output was retained.");
        }

        return crf;
    }
}
