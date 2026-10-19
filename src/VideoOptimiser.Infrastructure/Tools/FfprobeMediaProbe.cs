using System.Text.Json;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.Infrastructure.Tools;

public sealed class FfprobeMediaProbe(string ffprobePath) : IMediaProbe
{
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await ChildProcess.RunAsync(new ExternalProcessRequest(ffprobePath, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path]), cancellationToken);
        FfprobeOutput.EnsureSuccess(result.ExitCode, result.StandardError);
        try
        {
            return FfprobeJsonParser.Parse(result.StandardOutput);
        }
        catch (JsonException exception)
        {
            throw new MediaProbeException("ffprobe returned malformed output.", exception);
        }
    }
}

public static class FfprobeJsonParser
{
    public static MediaInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var streams = root.TryGetProperty("streams", out var streamArray) && streamArray.ValueKind == JsonValueKind.Array
            ? streamArray.EnumerateArray().ToArray()
            : [];

        var videoStreams = streams.Where(stream => GetString(stream, "codec_type") == "video").ToArray();
        var primaryVideo = videoStreams
            .Where(stream => !FfprobeOutput.IsAttachedPicture(stream))
            .OrderByDescending(FfprobeOutput.IsDefaultStream)
            .FirstOrDefault();
        if (primaryVideo.ValueKind == JsonValueKind.Undefined)
        {
            throw new MediaProbeException("ffprobe did not report a substantive video stream.");
        }

        var duration = root.TryGetProperty("format", out var format) ? GetDouble(format, "duration") : null;
        var size = root.TryGetProperty("format", out format) ? GetLong(format, "size") : null;
        return new MediaInfo(
            GetString(primaryVideo, "codec_name") ?? "unknown",
            videoStreams.Count(stream => !FfprobeOutput.IsAttachedPicture(stream)),
            streams.Count(stream => GetString(stream, "codec_type") == "audio"),
            streams.Count(stream => GetString(stream, "codec_type") == "subtitle"),
            streams.Count(stream => GetString(stream, "codec_type") == "attachment"),
            duration,
            size,
            GetInt(primaryVideo, "width"),
            GetInt(primaryVideo, "height"),
            GetLong(primaryVideo, "bit_rate") ?? OverallBitrate(format, size, duration),
            [.. streams.Where(stream => GetString(stream, "codec_type") == "audio").Select(stream => new AudioTrack(GetString(stream, "codec_name") ?? "unknown", GetString(stream, "codec_tag_string")))]);
    }

    /// <summary>
    /// Stands in for the video stream's bitrate when the container does not record one, as ffmpeg-muxed Matroska does not.
    /// It counts audio and subtitles as video, so it reads high: usual audio adds well under 1 Mbps, while lossless audio
    /// can lift a borderline video over a rule's minimum, which costs one CRF search that then finds it not worth converting.
    /// </summary>
    private static long? OverallBitrate(JsonElement format, long? size, double? duration) =>
        (format.ValueKind == JsonValueKind.Object ? GetLong(format, "bit_rate") : null)
        ?? (size > 0 && duration > 0 ? (long)(size.Value * 8 / duration.Value) : null);

    private static string? GetString(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetString() : null;

    private static int GetInt(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetInt32() : int.TryParse(value.GetString(), out var parsed) ? parsed : 0;

    private static int? GetInt(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? GetInt(value) : null;

    private static double? GetDouble(JsonElement element, string property) => element.TryGetProperty(property, out var value) && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static long? GetLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numericValue)) return numericValue;
        return long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
