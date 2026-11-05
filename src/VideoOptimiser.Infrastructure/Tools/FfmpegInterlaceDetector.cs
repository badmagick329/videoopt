using System.Globalization;
using System.Text.RegularExpressions;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.Infrastructure.Tools;

/// <summary>Runs FFmpeg's <c>idet</c> filter over the first 500 frames past the opening, and reads its single-frame counts.</summary>
public sealed partial class FfmpegInterlaceDetector(string ffmpegPath, IExternalProcessRunner processRunner) : IInterlaceDetector
{
    /// <summary>Enough frames for a verdict in a few seconds, even at 4K: over this many, the survey behind VOPT-199 put
    /// every flagged file either at 79% or more combed with one field first, or at 15% or less.</summary>
    internal const int Frames = 500;

    // Openings are often black or a still title card, which idet can't call. Only a video long enough to have more than an
    // opening skips one.
    private const double SkipSeconds = 30;
    private const double SkipFromDurationSeconds = 90;

    public async Task<FieldCounts> DetectAsync(string path, double? durationSeconds, CancellationToken cancellationToken = default)
    {
        var result = await processRunner.RunAsync(new ExternalProcessRequest(ffmpegPath, BuildArguments(path, durationSeconds)), cancellationToken);
        if (result.ExitCode != 0) throw new MediaProbeException($"ffmpeg could not check the video for interlacing (exit code {result.ExitCode}): {LastLine(result.StandardError)}");
        return Parse(result.StandardError) ?? throw new MediaProbeException("ffmpeg's interlace check printed no frame counts.");
    }

    internal static IReadOnlyList<string> BuildArguments(string path, double? durationSeconds)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-nostats" };
        if (durationSeconds > SkipFromDurationSeconds) arguments.AddRange(["-ss", SkipSeconds.ToString(CultureInfo.InvariantCulture)]);
        // V, not v: an attached picture such as cover art is never the video. idet reports at info level, so no -v.
        arguments.AddRange(["-i", path, "-map", "0:V:0", "-vf", "idet", "-frames:v", Frames.ToString(CultureInfo.InvariantCulture), "-an", "-sn", "-dn", "-f", "null", "-"]);
        return arguments;
    }

    /// <summary>The last report's counts, or null when there is none. FFmpeg configures the filter graph twice for some inputs,
    /// and the first graph's report is then all zeros.</summary>
    internal static FieldCounts? Parse(string standardError)
    {
        var match = SingleFrameLine().Matches(standardError).LastOrDefault();
        if (match is null) return null;
        int Count(string name) => int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture);
        return new FieldCounts(Count("tff"), Count("bff"), Count("progressive"), Count("undetermined"));
    }

    [GeneratedRegex(@"Single frame detection:\s*TFF:\s*(?<tff>\d+)\s+BFF:\s*(?<bff>\d+)\s+Progressive:\s*(?<progressive>\d+)\s+Undetermined:\s*(?<undetermined>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SingleFrameLine();

    // Without -v error, FFmpeg prints the input's streams first; its reason for failing comes last.
    private static string LastLine(string message) =>
        message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no error output";
}
