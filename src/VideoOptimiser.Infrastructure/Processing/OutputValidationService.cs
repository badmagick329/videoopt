using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Infrastructure.Processing;

public sealed class OutputValidationService(
    Func<string, IMediaProbe> mediaProbeFactory,
    Func<string, IMediaTimelineProbe> mediaTimelineProbeFactory,
    Func<string, IMediaDecodeTester> decodeTesterFactory) : IOutputValidationService
{
    private const double DecodeSampleSeconds = 10;

    public async Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        if (!File.Exists(manifest.SourcePath) || !File.Exists(manifest.OutputPath))
        {
            failures.Add("Original or temporary output is missing.");
            return new ValidationReport { Passed = false, Failures = failures, ValidatedUtc = DateTimeOffset.UtcNow };
        }

        var sourceSize = new FileInfo(manifest.SourcePath).Length;
        var outputSize = new FileInfo(manifest.OutputPath).Length;
        if (outputSize == 0)
        {
            failures.Add("Output is empty.");
            return new ValidationReport { Passed = false, Failures = failures, SourceSizeBytes = sourceSize, OutputSizeBytes = outputSize, ValidatedUtc = DateTimeOffset.UtcNow };
        }

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

        // A truncated or corrupt encode is exactly what validation exists to catch, so it is a failure, not a crash.
        MediaInfo output;
        try
        {
            output = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(manifest.OutputPath, cancellationToken);
        }
        catch (MediaProbeException exception)
        {
            failures.Add($"Output '{Path.GetFileName(manifest.OutputPath)}' could not be read by ffprobe: {exception.Message}");
            return new ValidationReport { Passed = false, Failures = failures, SourceSizeBytes = sourceSize, OutputSizeBytes = outputSize, ValidatedUtc = DateTimeOffset.UtcNow };
        }

        if (!output.PrimaryVideoCodec.Equals("av1", StringComparison.OrdinalIgnoreCase)) failures.Add("Output video is not AV1.");
        await ValidateMediaTimelineAsync(manifest, settings, source, output, failures, cancellationToken);
        if (source.AudioStreamCount != output.AudioStreamCount || source.SubtitleStreamCount != output.SubtitleStreamCount) failures.Add("Output stream counts differ from the original.");

        var saved = (decimal)(sourceSize - outputSize) / sourceSize * 100;
        if (settings.Savings.RequireSmallerOutput && outputSize >= sourceSize) failures.Add("Output is not smaller than the original.");
        if (saved < settings.Savings.MinimumPercentageSaved) failures.Add($"Output saves less than {settings.Savings.MinimumPercentageSaved}%.");
        if (!HumanReadableValues.TryParseSize(settings.Savings.MinimumBytesSaved, out var minimumBytesSaved))
        {
            throw new InvalidOperationException("savings.minimumBytesSaved must be validated before output validation.");
        }
        if (sourceSize - outputSize < minimumBytesSaved) failures.Add($"Output saves less than {settings.Savings.MinimumBytesSaved}.");

        // A full decode can take minutes, so it runs only for an output that would otherwise pass.
        if (failures.Count == 0) await ValidateDecodingAsync(manifest.OutputPath, settings, output.DurationSeconds, failures, cancellationToken);
        return new ValidationReport { Passed = failures.Count == 0, Failures = failures, SourceSizeBytes = sourceSize, OutputSizeBytes = outputSize, PercentageSaved = saved, ValidatedUtc = DateTimeOffset.UtcNow };
    }

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

    private async Task ValidateDecodingAsync(string outputPath, AppSettings settings, double? durationSeconds, List<string> failures, CancellationToken cancellationToken)
    {
        var tester = decodeTesterFactory(settings.Tools.FfmpegPath);
        foreach (var window in SelectDecodeWindows(settings.Validation.DecodeTest, durationSeconds))
        {
            try
            {
                var result = await tester.DecodeAsync(outputPath, window, cancellationToken);
                if (!result.Passed)
                {
                    failures.Add($"Output failed the decode test: {result.Error}");
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                failures.Add($"Output decode test could not run: {exception.Message}");
                return;
            }
        }
    }

    private async Task ValidateMediaTimelineAsync(
        OutputManifest manifest,
        AppSettings settings,
        MediaInfo source,
        MediaInfo output,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        try
        {
            var probe = mediaTimelineProbeFactory(settings.Tools.FfprobePath);
            var sourceTimeline = await probe.ProbeAsync(manifest.SourcePath, cancellationToken);
            var outputTimeline = await probe.ProbeAsync(manifest.OutputPath, cancellationToken);
            var tolerance = Math.Max(0.001d, settings.Validation.DurationToleranceSeconds);
            if (Math.Abs(sourceTimeline.PrimaryVideoDurationSeconds - outputTimeline.PrimaryVideoDurationSeconds) > tolerance)
            {
                failures.Add("Output duration differs from the original.");
            }

            if (!HasMaterialPacketCountDifference(sourceTimeline, outputTimeline, tolerance, out var packetCountsAreReliable))
            {
                if (!packetCountsAreReliable)
                {
                    failures.Add("Output media timeline could not be validated.");
                }
            }
            else
            {
                failures.Add("Output video packet count differs from the original.");
            }

            if (source.AudioStreamCount > 0 &&
                (sourceTimeline.FirstPrimaryAudioTimestampSeconds is null || outputTimeline.FirstPrimaryAudioTimestampSeconds is null ||
                 Math.Abs((sourceTimeline.FirstPrimaryAudioTimestampSeconds.Value - sourceTimeline.FirstVideoTimestampSeconds) -
                          (outputTimeline.FirstPrimaryAudioTimestampSeconds.Value - outputTimeline.FirstVideoTimestampSeconds)) > tolerance))
            {
                failures.Add("Output audio timing differs from the original.");
            }
        }
        catch (Exception exception) when (exception is IOException or MediaProbeException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            failures.Add("Output media timeline could not be validated.");
        }
    }

    private static bool HasMaterialPacketCountDifference(MediaTimeline source, MediaTimeline output, double tolerance, out bool countsAreReliable)
    {
        countsAreReliable =
            source.PrimaryVideoPacketCount > 0 &&
            output.PrimaryVideoPacketCount > 0 &&
            source.PrimaryVideoDurationSeconds > 0 &&
            output.PrimaryVideoDurationSeconds > 0;
        if (!countsAreReliable)
        {
            return false;
        }

        var sourcePacketDuration = source.PrimaryVideoDurationSeconds / source.PrimaryVideoPacketCount;
        var outputPacketDuration = output.PrimaryVideoDurationSeconds / output.PrimaryVideoPacketCount;
        if (!double.IsFinite(sourcePacketDuration) || !double.IsFinite(outputPacketDuration) || sourcePacketDuration <= 0 || outputPacketDuration <= 0)
        {
            countsAreReliable = false;
            return false;
        }

        var difference = Math.Abs(source.PrimaryVideoPacketCount - output.PrimaryVideoPacketCount);
        var approximateMissingDuration = difference * Math.Max(sourcePacketDuration, outputPacketDuration);
        return approximateMissingDuration > tolerance;
    }
}
