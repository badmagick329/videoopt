using System.Diagnostics;
using System.Text.Json;
using VideoOptimiser.Application.Scanning;

namespace VideoOptimiser.Infrastructure.Scanning;

public sealed class FfprobeMediaProbe(string ffprobePath) : IMediaProbe
{
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add(path);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start ffprobe at '{ffprobePath}'.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"ffprobe exited with {process.ExitCode}: {FirstLine(error)}");
        }

        return FfprobeJsonParser.Parse(output);
    }

    private static string FirstLine(string message) => message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "No error output.";
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
            .Where(stream => !IsAttachedPicture(stream))
            .OrderByDescending(IsDefaultStream)
            .FirstOrDefault();
        if (primaryVideo.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("ffprobe did not report a substantive video stream.");
        }

        var duration = root.TryGetProperty("format", out var format) ? GetDouble(format, "duration") : null;
        var size = root.TryGetProperty("format", out format) ? GetLong(format, "size") : null;
        return new MediaInfo(
            GetString(primaryVideo, "codec_name") ?? "unknown",
            videoStreams.Count(stream => !IsAttachedPicture(stream)),
            streams.Count(stream => GetString(stream, "codec_type") == "audio"),
            streams.Count(stream => GetString(stream, "codec_type") == "subtitle"),
            streams.Count(stream => GetString(stream, "codec_type") == "attachment"),
            duration,
            size);
    }

    private static bool IsDefaultStream(JsonElement stream) => stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("default", out var value) && GetInt(value) == 1;

    private static bool IsAttachedPicture(JsonElement stream) => stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("attached_pic", out var value) && GetInt(value) == 1;

    private static string? GetString(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetString() : null;

    private static int GetInt(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetInt32() : int.TryParse(value.GetString(), out var parsed) ? parsed : 0;

    private static double? GetDouble(JsonElement element, string property) => element.TryGetProperty(property, out var value) && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static long? GetLong(JsonElement element, string property) => element.TryGetProperty(property, out var value) && long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
