using System.Globalization;
using System.Text.Json;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.Infrastructure.Tools;

public sealed class FfprobeMediaTimelineProbe(string ffprobePath) : IMediaTimelineProbe
{
    public async Task<MediaTimeline> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var streams = await ReadStreamSelectionAsync(path, cancellationToken);
        return await ReadPacketTimelineAsync(path, streams, cancellationToken);
    }

    private async Task<PrimaryStreams> ReadStreamSelectionAsync(string path, CancellationToken cancellationToken)
    {
        var result = await ChildProcess.RunAsync(new ExternalProcessRequest(ffprobePath, BuildStreamSelectionArguments(path)), cancellationToken);
        FfprobeOutput.EnsureSuccess(result.ExitCode, result.StandardError);

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (!document.RootElement.TryGetProperty("streams", out var streamArray) || streamArray.ValueKind != JsonValueKind.Array)
            {
                throw new MediaProbeException("ffprobe did not report streams for timeline validation.");
            }

            var streams = streamArray.EnumerateArray().ToArray();
            var video = streams
                .Where(stream => TypeIs(stream, "video") && !FfprobeOutput.IsAttachedPicture(stream))
                .OrderByDescending(FfprobeOutput.IsDefaultStream)
                .FirstOrDefault();
            if (video.ValueKind == JsonValueKind.Undefined || !FfprobeOutput.TryGetInt(video, "index", out var videoIndex))
            {
                throw new MediaProbeException("ffprobe did not report a primary video stream for timeline validation.");
            }

            var audio = streams
                .Where(stream => TypeIs(stream, "audio"))
                .OrderByDescending(FfprobeOutput.IsDefaultStream)
                .FirstOrDefault();
            var audioIndex = audio.ValueKind != JsonValueKind.Undefined && FfprobeOutput.TryGetInt(audio, "index", out var index)
                ? index
                : (int?)null;
            return new PrimaryStreams(videoIndex, audioIndex);
        }
        catch (JsonException exception)
        {
            throw new MediaProbeException("ffprobe returned malformed stream metadata for timeline validation.", exception);
        }
    }

    private async Task<MediaTimeline> ReadPacketTimelineAsync(string path, PrimaryStreams streams, CancellationToken cancellationToken)
    {
        // Packet lines are folded in as they arrive: a long video has hundreds of thousands, too many to hold as text.
        // Malformed timing throws from Add; ffprobe is then drained to exit and that exception is the one reported.
        var accumulator = new PacketTimelineAccumulator(streams);
        var errorLines = new List<string>();
        var exitCode = await ChildProcess.StreamAsync(new ExternalProcessRequest(ffprobePath, BuildPacketTimelineArguments(path)), accumulator.Add, errorLines.Add, cancellationToken);
        FfprobeOutput.EnsureSuccess(exitCode, string.Join(Environment.NewLine, errorLines));
        return accumulator.Build();
    }

    internal static IReadOnlyList<string> BuildStreamSelectionArguments(string path) =>
    [
        "-v", "error",
        "-show_streams",
        "-show_entries", "stream=index,codec_type,disposition",
        "-print_format", "json",
        path
    ];

    internal static IReadOnlyList<string> BuildPacketTimelineArguments(string path) =>
    [
        "-v", "error",
        "-show_packets",
        "-show_entries", "packet=stream_index,pts_time,duration_time",
        "-of", "compact=p=0:nk=1",
        path
    ];

    internal static MediaTimeline ParseCompactPacketLines(IEnumerable<string> lines, int primaryVideoStreamIndex, int? primaryAudioStreamIndex)
    {
        var accumulator = new PacketTimelineAccumulator(new PrimaryStreams(primaryVideoStreamIndex, primaryAudioStreamIndex));
        foreach (var line in lines)
        {
            accumulator.Add(line);
        }

        return accumulator.Build();
    }

    private static bool TypeIs(JsonElement stream, string type) => stream.TryGetProperty("codec_type", out var value) && string.Equals(value.GetString(), type, StringComparison.OrdinalIgnoreCase);

    private sealed record PrimaryStreams(int VideoIndex, int? AudioIndex);

    private sealed class PacketTimelineAccumulator(PrimaryStreams streams)
    {
        private double _firstVideoTimestamp = double.PositiveInfinity;
        private double _lastVideoEndTimestamp = double.NegativeInfinity;
        private double _firstAudioTimestamp = double.PositiveInfinity;
        private bool _sawVideo;

        public void Add(string line)
        {
            var fields = line.Split('|');
            if (fields.Length != 3 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var streamIndex))
            {
                return;
            }

            if (streamIndex == streams.VideoIndex)
            {
                if (!TryParseTimestamp(fields[1], out var pts) || !TryParseTimestamp(fields[2], out var duration) || duration < 0)
                {
                    throw new MediaProbeException("ffprobe reported malformed primary video packet timing.");
                }

                _firstVideoTimestamp = Math.Min(_firstVideoTimestamp, pts);
                _lastVideoEndTimestamp = Math.Max(_lastVideoEndTimestamp, pts + duration);
                _sawVideo = true;
            }
            else if (streamIndex == streams.AudioIndex)
            {
                if (!TryParseTimestamp(fields[1], out var pts))
                {
                    throw new MediaProbeException("ffprobe reported malformed primary audio packet timing.");
                }

                _firstAudioTimestamp = Math.Min(_firstAudioTimestamp, pts);
            }
        }

        public MediaTimeline Build()
        {
            if (!_sawVideo || !double.IsFinite(_firstVideoTimestamp) || !double.IsFinite(_lastVideoEndTimestamp) || _lastVideoEndTimestamp < _firstVideoTimestamp)
            {
                throw new MediaProbeException("ffprobe did not report complete primary video packet timing.");
            }

            return new MediaTimeline(
                _firstVideoTimestamp,
                _lastVideoEndTimestamp,
                double.IsFinite(_firstAudioTimestamp) ? _firstAudioTimestamp : null);
        }

        private static bool TryParseTimestamp(string value, out double timestamp) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out timestamp) && double.IsFinite(timestamp);
    }
}
