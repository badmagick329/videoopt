using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.Infrastructure.Processing;

public sealed class AbAv1CrfSearchClient(string executablePath) : ICrfSearchClient
{
    public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) =>
    [
        "crf-search", "--input", inputPath,
        "--encoder", settings.Encoder,
        "--pix-format", settings.PixelFormat,
        "--preset", settings.Preset.ToString(CultureInfo.InvariantCulture),
        "--min-vmaf", settings.MinimumVmaf.ToString(CultureInfo.InvariantCulture),
        "--min-crf", settings.CrfSearch.MinCrf.ToString(CultureInfo.InvariantCulture),
        "--max-crf", settings.CrfSearch.MaxCrf.ToString(CultureInfo.InvariantCulture),
        "--samples", settings.CrfSearch.SampleCount.ToString(CultureInfo.InvariantCulture),
        "--sample-duration", settings.CrfSearch.SampleDuration
    ];

    public async Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in BuildArguments(inputPath, settings))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var started = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start ab-av1.");
        }

        var outputTask = ReadLinesAsync(process.StandardOutput, "stdout", progress, cancellationToken);
        var errorTask = ReadLinesAsync(process.StandardError, "stderr", progress, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        started.Stop();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ab-av1 crf-search failed with exit code {process.ExitCode}: {FirstLine(error)}");
        }

        return new CrfSearchResult(CrfSearchOutputParser.Parse(output), output, error, started.Elapsed);
    }

    private static string FirstLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "No error output.";

    private static async Task<string> ReadLinesAsync(StreamReader reader, string stream, IProgress<CrfSearchOutput>? progress, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            progress?.Report(new CrfSearchOutput(stream, line));
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public static partial class CrfSearchOutputParser
{
    [GeneratedRegex(@"(?im)^\s*(?:best\s+)?crf(?:\s+value)?\s*[:=]\s*(?<crf>\d+(?:\.0+)?)\s*$")]
    private static partial Regex CrfLine();

    [GeneratedRegex(@"(?im)\bcrf\s+(?<crf>\d+(?:\.0+)?)\s+successful\s*$")]
    private static partial Regex SuccessfulCrfLine();

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
