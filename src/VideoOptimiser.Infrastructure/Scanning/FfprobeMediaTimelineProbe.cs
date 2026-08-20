using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using VideoOptimiser.Application.Scanning;

namespace VideoOptimiser.Infrastructure.Scanning;

public sealed class FfprobeMediaTimelineProbe(string ffprobePath) : IMediaTimelineProbe
{
    public async Task<MediaTimeline> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var streams = await ReadStreamSelectionAsync(path, cancellationToken);
        return await ReadPacketTimelineAsync(path, streams, cancellationToken);
    }

    private async Task<PrimaryStreams> ReadStreamSelectionAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(BuildStreamSelectionArguments(path));
        using var process = Start(startInfo);
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            EnsureSuccess(process, error);

            try
            {
                using var document = JsonDocument.Parse(output);
                if (!document.RootElement.TryGetProperty("streams", out var streamArray) || streamArray.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("ffprobe did not report streams for timeline validation.");
                }

                var streams = streamArray.EnumerateArray().ToArray();
                var video = streams
                    .Where(stream => TypeIs(stream, "video") && !IsAttachedPicture(stream))
                    .OrderByDescending(IsDefaultStream)
                    .FirstOrDefault();
                if (video.ValueKind == JsonValueKind.Undefined || !TryGetInt(video, "index", out var videoIndex))
                {
                    throw new InvalidDataException("ffprobe did not report a primary video stream for timeline validation.");
                }

                var audio = streams
                    .Where(stream => TypeIs(stream, "audio"))
                    .OrderByDescending(IsDefaultStream)
                    .FirstOrDefault();
                var audioIndex = audio.ValueKind != JsonValueKind.Undefined && TryGetInt(audio, "index", out var index)
                    ? index
                    : (int?)null;
                return new PrimaryStreams(videoIndex, audioIndex);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("ffprobe returned malformed stream metadata for timeline validation.", exception);
            }
        }
        catch
        {
            await StopAsync(process);
            throw;
        }
    }

    private async Task<MediaTimeline> ReadPacketTimelineAsync(string path, PrimaryStreams streams, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(BuildPacketTimelineArguments(path));
        using var process = Start(startInfo);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var accumulator = new PacketTimelineAccumulator(streams);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                accumulator.Add(line);
            }

            await process.WaitForExitAsync(cancellationToken);
            EnsureSuccess(process, await errorTask);
            return accumulator.Build();
        }
        catch
        {
            await StopAsync(process);
            throw;
        }
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

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static Process Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Could not start ffprobe at '{startInfo.FileName}'.");
        }

        return process;
    }

    private static void EnsureSuccess(Process process, string error)
    {
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"ffprobe exited with {process.ExitCode}: {FirstLine(error)}");
        }
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static string FirstLine(string message) => message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "No error output.";
    private static bool TypeIs(JsonElement stream, string type) => stream.TryGetProperty("codec_type", out var value) && string.Equals(value.GetString(), type, StringComparison.OrdinalIgnoreCase);
    private static bool IsDefaultStream(JsonElement stream) => stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("default", out var value) && TryGetInt(value, out var result) && result == 1;
    private static bool IsAttachedPicture(JsonElement stream) => stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("attached_pic", out var value) && TryGetInt(value, out var result) && result == 1;
    private static bool TryGetInt(JsonElement element, string property, out int value)
    {
        value = 0;
        return element.TryGetProperty(property, out var propertyValue) && TryGetInt(propertyValue, out value);
    }
    private static bool TryGetInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number
            ? element.TryGetInt32(out value)
            : int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private sealed record PrimaryStreams(int VideoIndex, int? AudioIndex);

    private sealed class PacketTimelineAccumulator(PrimaryStreams streams)
    {
        private double _firstVideoTimestamp = double.PositiveInfinity;
        private double _lastVideoEndTimestamp = double.NegativeInfinity;
        private double _firstAudioTimestamp = double.PositiveInfinity;
        private long _videoPacketCount;

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
                    throw new InvalidDataException("ffprobe reported malformed primary video packet timing.");
                }

                _firstVideoTimestamp = Math.Min(_firstVideoTimestamp, pts);
                _lastVideoEndTimestamp = Math.Max(_lastVideoEndTimestamp, pts + duration);
                _videoPacketCount++;
            }
            else if (streamIndex == streams.AudioIndex)
            {
                if (!TryParseTimestamp(fields[1], out var pts))
                {
                    throw new InvalidDataException("ffprobe reported malformed primary audio packet timing.");
                }

                _firstAudioTimestamp = Math.Min(_firstAudioTimestamp, pts);
            }
        }

        public MediaTimeline Build()
        {
            if (_videoPacketCount == 0 || !double.IsFinite(_firstVideoTimestamp) || !double.IsFinite(_lastVideoEndTimestamp) || _lastVideoEndTimestamp < _firstVideoTimestamp)
            {
                throw new InvalidDataException("ffprobe did not report complete primary video packet timing.");
            }

            return new MediaTimeline(
                _firstVideoTimestamp,
                _lastVideoEndTimestamp,
                _videoPacketCount,
                double.IsFinite(_firstAudioTimestamp) ? _firstAudioTimestamp : null);
        }

        private static bool TryParseTimestamp(string value, out double timestamp) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out timestamp) && double.IsFinite(timestamp);
    }
}
