using System.Diagnostics;
using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.Infrastructure.Processing;

public sealed class AbAv1VideoEncoder(string executablePath) : IVideoEncoder
{
    public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int crf, QualitySettings settings) =>
    [
        "encode", "--input", inputPath, "--output", outputPath,
        "--encoder", settings.Encoder,
        "--pix-format", settings.PixelFormat,
        "--preset", settings.Preset.ToString(CultureInfo.InvariantCulture),
        "--crf", crf.ToString(CultureInfo.InvariantCulture),
        // Container-specific data streams such as timecode tracks are not generally portable to the output container.
        "--enc", "map=-0:d",
        // ffmpeg writes no source metadata unless told to. Without these the output loses its capture date
        // (creation_time), GPS (location) and the phone's make/model (com.android.* keys, which only
        // use_metadata_tags writes), and photo libraries such as Immich date and place it wrongly.
        // Setting movflags replaces ab-av1's own +faststart, so it is repeated: the index stays ahead of the media
        // for streaming, and a truncated output still probes and fails validation cleanly.
        "--enc", "map_metadata=0",
        "--enc", "movflags=+faststart+use_metadata_tags"
    ];

    public async Task<EncodeResult> EncodeAsync(string inputPath, string outputPath, int crf, QualitySettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo { FileName = executablePath, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in BuildArguments(inputPath, outputPath, crf, settings))
        {
            info.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = info };
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

        _ = await Task.WhenAll(outputTask, errorTask);
        stopwatch.Stop();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ab-av1 encode failed with exit code {process.ExitCode}.");
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidDataException("ab-av1 exited successfully but did not create a non-empty output file.");
        }

        return new EncodeResult(outputPath, stopwatch.Elapsed);
    }

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
