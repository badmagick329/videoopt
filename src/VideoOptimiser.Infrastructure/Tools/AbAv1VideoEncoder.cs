using System.Diagnostics;
using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.Infrastructure.Tools;

public sealed class AbAv1VideoEncoder(string executablePath) : IVideoEncoder
{
    public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int crf, QualitySettings settings, AudioPlan audio) =>
    [
        "encode", "--input", inputPath, "--output", outputPath,
        "--encoder", settings.Encoder,
        "--pix-format", settings.PixelFormat,
        "--preset", settings.Preset.ToString(CultureInfo.InvariantCulture),
        "--crf", crf.ToString(CultureInfo.InvariantCulture),
        // Container-specific data streams such as timecode tracks are not generally portable to the output container.
        "--enc", "map=-0:d",
        // FFmpeg cannot identify these streams (iPhone spatial audio), so the muxer has no tag to write for them and refuses
        // the whole output. The index names an input stream, so it is the original's audio-relative one.
        .. audio.Dropped.SelectMany(track => new[] { "--enc", $"map=-0:a:{track.Index.ToString(CultureInfo.InvariantCulture)}" }),
        // Every frame keeps its own timestamp. Writing MP4, ffmpeg otherwise forces the header's nominal frame rate, which a
        // phone's variable-rate video only approximates: one at 30.01 fps on average but tagged 29.75 lost a frame every 4
        // seconds, and validation failed it on packet count.
        "--enc", "fps_mode=passthrough",
        // ffmpeg writes no source metadata unless told to. Without these the output loses its capture date
        // (creation_time), GPS (location) and the phone's make/model (com.android.* keys, which only
        // use_metadata_tags writes), and photo libraries such as Immich date and place it wrongly.
        // Setting movflags replaces ab-av1's own +faststart, so it is repeated: the index stays ahead of the media
        // for streaming, and a truncated output still probes and fails validation cleanly.
        "--enc", "map_metadata=0",
        "--enc", "movflags=+faststart+use_metadata_tags",
        // ab-av1 copies audio. PCM copied into MP4 is tagged ipcm, which browsers cannot play, so those streams become
        // lossless FLAC, which MP4 and Matroska both carry and browsers play. The indexes are output-relative (past any
        // dropped stream), because -c:a:N names an output stream.
        .. audio.FlacOutputStreams.SelectMany(index => new[] { "--enc", $"c:a:{index.ToString(CultureInfo.InvariantCulture)}=flac" })
    ];

    public async Task<EncodeResult> EncodeAsync(string inputPath, string outputPath, int crf, QualitySettings settings, AudioPlan audio, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        void Report(string line) => progress?.Report(JobProgress.Tool(line));
        var exitCode = await ChildProcess.StreamAsync(new ExternalProcessRequest(executablePath, BuildArguments(inputPath, outputPath, crf, settings, audio)), Report, Report, cancellationToken);
        stopwatch.Stop();
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ab-av1 encode failed with exit code {exitCode}.");
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidDataException("ab-av1 exited successfully but did not create a non-empty output file.");
        }

        return new EncodeResult(outputPath, stopwatch.Elapsed);
    }
}
