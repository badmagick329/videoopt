using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;

namespace VideoOptimiser.Infrastructure.Processing;

public sealed class OutputValidationService(
    Func<string, IMediaProbe> mediaProbeFactory,
    Func<string, IMediaTimelineProbe> mediaTimelineProbeFactory) : IOutputValidationService
{
    public async Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        if (!File.Exists(manifest.SourcePath) || !File.Exists(manifest.OutputPath))
        {
            failures.Add("Source or temporary output is missing.");
            return new ValidationReport { Passed = false, Failures = failures, ValidatedUtc = DateTimeOffset.UtcNow };
        }

        var source = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(manifest.SourcePath, cancellationToken);
        var output = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(manifest.OutputPath, cancellationToken);
        if (!output.PrimaryVideoCodec.Equals("av1", StringComparison.OrdinalIgnoreCase)) failures.Add("Output video is not AV1.");
        await ValidateMediaTimelineAsync(manifest, settings, source, output, failures, cancellationToken);
        if (source.AudioStreamCount != output.AudioStreamCount || source.SubtitleStreamCount != output.SubtitleStreamCount) failures.Add("Output stream counts differ from source.");

        var sourceSize = new FileInfo(manifest.SourcePath).Length;
        var outputSize = new FileInfo(manifest.OutputPath).Length;
        var saved = (decimal)(sourceSize - outputSize) / sourceSize * 100;
        if ((settings.Savings.RequireSmallerOutput && outputSize >= sourceSize) || saved < settings.Savings.MinimumPercentageSaved) failures.Add("Output does not meet savings policy.");
        return new ValidationReport { Passed = failures.Count == 0, Failures = failures, SourceSizeBytes = sourceSize, OutputSizeBytes = outputSize, PercentageSaved = saved, ValidatedUtc = DateTimeOffset.UtcNow };
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
                failures.Add("Output duration differs from source.");
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
                failures.Add("Output video packet count differs from source.");
            }

            if (source.AudioStreamCount > 0 &&
                (sourceTimeline.FirstPrimaryAudioTimestampSeconds is null || outputTimeline.FirstPrimaryAudioTimestampSeconds is null ||
                 Math.Abs((sourceTimeline.FirstPrimaryAudioTimestampSeconds.Value - sourceTimeline.FirstVideoTimestampSeconds) -
                          (outputTimeline.FirstPrimaryAudioTimestampSeconds.Value - outputTimeline.FirstVideoTimestampSeconds)) > tolerance))
            {
                failures.Add("Output audio timing differs from source.");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
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
