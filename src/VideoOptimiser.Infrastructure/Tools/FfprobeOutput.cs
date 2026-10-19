using System.Globalization;
using System.Text.Json;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.Infrastructure.Tools;

/// <summary>What <see cref="FfprobeMediaProbe"/>, <see cref="FfprobeJsonParser"/> and <see cref="FfprobeMediaTimelineProbe"/> read the same way.</summary>
internal static class FfprobeOutput
{
    /// <summary>A non-zero exit is a probe failure (an unreadable original), reported with ffprobe's first error line.</summary>
    public static void EnsureSuccess(int exitCode, string error)
    {
        if (exitCode != 0) throw new MediaProbeException($"ffprobe exited with {exitCode}: {FirstLine(error)}");
    }

    private static string FirstLine(string message) => message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "No error output.";

    public static bool IsDefaultStream(JsonElement stream) => DispositionIsSet(stream, "default");

    /// <summary>Cover art: a video stream holding one still image, never the video itself.</summary>
    public static bool IsAttachedPicture(JsonElement stream) => DispositionIsSet(stream, "attached_pic");

    private static bool DispositionIsSet(JsonElement stream, string flag) =>
        stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty(flag, out var value) && TryGetInt(value, out var result) && result == 1;

    public static bool TryGetInt(JsonElement element, string property, out int value)
    {
        value = 0;
        return element.TryGetProperty(property, out var propertyValue) && TryGetInt(propertyValue, out value);
    }

    /// <summary>ffprobe writes some integers as JSON numbers and some as strings, depending on the field and the version.</summary>
    public static bool TryGetInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }
}
