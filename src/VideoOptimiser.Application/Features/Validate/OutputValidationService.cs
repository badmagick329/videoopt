using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Features.Validate;

/// <summary>
/// Checks a finished AV1 against its original and gives a <see cref="ValidationVerdict"/> (VOPT-116). A check fails the AV1
/// only when there is nothing to review; a size that saves too little makes the video not worth converting; anything else
/// that looks wrong is a warning for the user, who looks at every video before finalising it. The download is already
/// checked byte for byte, so what remains to catch is an encode that did not finish, which shows as a length or stream
/// difference.
/// </summary>
public sealed class OutputValidationService(
    Func<string, IMediaProbe> mediaProbeFactory,
    Func<string, IMediaTimelineProbe> mediaTimelineProbeFactory,
    Func<string, IMediaDecodeTester> decodeTesterFactory) : IOutputValidationService
{
    private const double DecodeSampleSeconds = 10;

    public async Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifest.SourcePath) || !File.Exists(manifest.OutputPath)) return Failed("Original or temporary output is missing.");

        var sourceSize = new FileInfo(manifest.SourcePath).Length;
        var outputSize = new FileInfo(manifest.OutputPath).Length;
        if (outputSize == 0) return Failed("Output is empty.", sourceSize, outputSize);

        // An original ffprobe cannot read says nothing about the output, so it stays an error rather than a verdict on it.
        MediaInfo source;
        try
        {
            source = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(manifest.SourcePath, cancellationToken);
        }
        catch (MediaProbeException exception)
        {
            throw new MediaProbeException($"Original '{Path.GetFileName(manifest.SourcePath)}' could not be read by ffprobe: {exception.Message}", exception);
        }

        MediaInfo output;
        try
        {
            output = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(manifest.OutputPath, cancellationToken);
        }
        catch (MediaProbeException exception)
        {
            return Failed($"Output '{Path.GetFileName(manifest.OutputPath)}' could not be read by ffprobe: {exception.Message}", sourceSize, outputSize);
        }
        if (!output.PrimaryVideoCodec.Equals("av1", StringComparison.OrdinalIgnoreCase)) return Failed("Output video is not AV1.", sourceSize, outputSize);

        var saved = (decimal)(sourceSize - outputSize) / sourceSize * 100;
        // Checked before the timeline and the decode test: an AV1 that is deleted anyway needs neither.
        if (NotWorthConverting(sourceSize, outputSize, settings.Savings) is { } reason)
        {
            return new ValidationReport { Passed = false, NotWorthConverting = reason, SourceSizeBytes = sourceSize, OutputSizeBytes = outputSize, PercentageSaved = saved, ValidatedUtc = DateTimeOffset.UtcNow };
        }

        var warnings = new List<string>();
        await CompareTimelinesAsync(manifest, settings, source, warnings, cancellationToken);
        // The encode leaves out audio FFmpeg cannot identify, so the AV1 should have the original's count less those.
        AddStreamCountWarning(warnings, "audio", AudioPlan.For(source).KeptAudioStreamCount, output.AudioStreamCount);
        AddStreamCountWarning(warnings, "subtitle", source.SubtitleStreamCount, output.SubtitleStreamCount);
        // A warned AV1 is decoded too, so a decode problem is one more thing the user sees, not hidden behind the first doubt.
        await TestDecodingAsync(manifest.OutputPath, settings, output.DurationSeconds, warnings, cancellationToken);
        // Last, so the checksum is of the file every check above read. Here rather than where the record is saved: that step
        // takes no token, and hashing a large AV1 should stay cancellable like the rest of validation.
        var checksum = await ImmichStaging.Sha1Base64Async(manifest.OutputPath, cancellationToken);
        return new ValidationReport { Passed = true, Warnings = warnings, SourceSizeBytes = sourceSize, OutputSizeBytes = outputSize, PercentageSaved = saved, ValidatedUtc = DateTimeOffset.UtcNow, Av1Checksum = checksum };
    }

    private static ValidationReport Failed(string failure, long sourceSize = 0, long outputSize = 0) =>
        new() { Passed = false, Failures = [failure], SourceSizeBytes = sourceSize, OutputSizeBytes = outputSize, ValidatedUtc = DateTimeOffset.UtcNow };

    /// <summary>The reason, naming the setting, or null when the AV1 is worth keeping. The same rules decide a CRF search's skip.</summary>
    internal static string? NotWorthConverting(long sourceSize, long outputSize, SavingsSettings savings)
    {
        if (!HumanReadableValues.TryParseSize(savings.MinimumBytesSaved, out var minimumBytesSaved))
        {
            throw new InvalidOperationException("savings.minimumBytesSaved must be validated before output validation.");
        }
        var notSmaller = $"the AV1 is {HumanReadableValues.FormatSize(outputSize)}, no smaller than the original's {HumanReadableValues.FormatSize(sourceSize)}";
        if (savings.RequireSmallerOutput && outputSize >= sourceSize) return $"{notSmaller} (savings.requireSmallerOutput).";
        if (sourceSize - outputSize >= minimumBytesSaved) return null;
        return outputSize >= sourceSize
            ? $"{notSmaller}, so it can't save {savings.MinimumBytesSaved} (savings.minimumBytesSaved)."
            : $"the AV1 saves only {HumanReadableValues.FormatSize(sourceSize - outputSize)}, less than {savings.MinimumBytesSaved} (savings.minimumBytesSaved).";
    }

    private static void AddStreamCountWarning(List<string> warnings, string kind, int source, int output)
    {
        if (source != output) warnings.Add($"The AV1 has {Streams(output, kind)}; the original has {source.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static string Streams(int count, string kind) => $"{count.ToString(CultureInfo.InvariantCulture)} {kind} {(count == 1 ? "stream" : "streams")}";

    /// <summary>
    /// Sampled mode decodes the start, middle and end: ffprobe reads only container metadata, and a damaged
    /// encode or transfer most often loses the tail. Videos too short to sample, or of unknown length, decode whole.
    /// </summary>
    internal static IReadOnlyList<DecodeWindow> SelectDecodeWindows(string mode, double? durationSeconds)
    {
        if (mode.Equals(DecodeTestModes.None, StringComparison.OrdinalIgnoreCase)) return [];
        if (mode.Equals(DecodeTestModes.Full, StringComparison.OrdinalIgnoreCase) || durationSeconds is not { } duration || duration <= DecodeSampleSeconds * 3)
        {
            return [DecodeWindow.Whole];
        }

        return
        [
            new DecodeWindow(0, DecodeSampleSeconds),
            new DecodeWindow((duration - DecodeSampleSeconds) / 2, DecodeSampleSeconds),
            new DecodeWindow(duration - DecodeSampleSeconds, null)
        ];
    }

    private async Task TestDecodingAsync(string outputPath, AppSettings settings, double? durationSeconds, List<string> warnings, CancellationToken cancellationToken)
    {
        var tester = decodeTesterFactory(settings.Tools.FfmpegPath);
        foreach (var window in SelectDecodeWindows(settings.Validation.DecodeTest, durationSeconds))
        {
            try
            {
                var result = await tester.DecodeAsync(outputPath, window, cancellationToken);
                if (!result.Passed)
                {
                    warnings.Add($"The AV1 failed the decode test: {result.Error}");
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                warnings.Add($"The decode test could not run: {OneLine(exception.Message)}");
                return;
            }
        }
    }

    /// <summary>
    /// Compares the primary video's length and where the primary audio starts relative to it. A shift of both streams by the
    /// same amount is no difference. The video packet count is not compared: it differed on every variable-frame-rate phone
    /// video while the length matched (VOPT-116).
    /// </summary>
    private async Task CompareTimelinesAsync(OutputManifest manifest, AppSettings settings, MediaInfo source, List<string> warnings, CancellationToken cancellationToken)
    {
        MediaTimeline sourceTimeline, outputTimeline;
        try
        {
            var probe = mediaTimelineProbeFactory(settings.Tools.FfprobePath);
            sourceTimeline = await probe.ProbeAsync(manifest.SourcePath, cancellationToken);
            outputTimeline = await probe.ProbeAsync(manifest.OutputPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or MediaProbeException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            warnings.Add($"The length and audio sync could not be checked: {OneLine(exception.Message)}");
            return;
        }

        var tolerance = Math.Max(0.001d, settings.Validation.DurationToleranceSeconds);
        var lengthDifference = outputTimeline.PrimaryVideoDurationSeconds - sourceTimeline.PrimaryVideoDurationSeconds;
        if (Math.Abs(lengthDifference) > tolerance)
        {
            warnings.Add($"The AV1's video is {Seconds(Math.Abs(lengthDifference))} {(lengthDifference < 0 ? "shorter" : "longer")} than the original's.");
        }

        if (source.AudioStreamCount == 0) return;
        if (sourceTimeline.FirstPrimaryAudioTimestampSeconds is not { } sourceAudio || outputTimeline.FirstPrimaryAudioTimestampSeconds is not { } outputAudio)
        {
            warnings.Add("The audio's start time could not be read, so audio sync was not checked.");
            return;
        }
        var drift = (outputAudio - outputTimeline.FirstVideoTimestampSeconds) - (sourceAudio - sourceTimeline.FirstVideoTimestampSeconds);
        if (Math.Abs(drift) > tolerance)
        {
            warnings.Add($"The AV1's audio starts {Seconds(Math.Abs(drift))} {(drift < 0 ? "earlier" : "later")} relative to the video than in the original.");
        }
    }

    private static string Seconds(double seconds) => $"{seconds.ToString("0.###", CultureInfo.InvariantCulture)} s";

    private static string OneLine(string message) =>
        message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? message;
}
