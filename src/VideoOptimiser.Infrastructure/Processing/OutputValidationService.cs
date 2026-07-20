using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;

namespace VideoOptimiser.Infrastructure.Processing;

public sealed class OutputValidationService(Func<string, IMediaProbe> mediaProbeFactory) : IOutputValidationService
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
        if (Math.Abs((source.DurationSeconds ?? 0) - (output.DurationSeconds ?? 0)) > settings.Validation.DurationToleranceSeconds) failures.Add("Output duration differs from source.");
        if (source.AudioStreamCount != output.AudioStreamCount || source.SubtitleStreamCount != output.SubtitleStreamCount) failures.Add("Output stream counts differ from source.");

        var sourceSize = new FileInfo(manifest.SourcePath).Length;
        var outputSize = new FileInfo(manifest.OutputPath).Length;
        var saved = (decimal)(sourceSize - outputSize) / sourceSize * 100;
        if ((settings.Savings.RequireSmallerOutput && outputSize >= sourceSize) || saved < settings.Savings.MinimumPercentageSaved) failures.Add("Output does not meet savings policy.");
        return new ValidationReport { Passed = failures.Count == 0, Failures = failures, SourceSizeBytes = sourceSize, OutputSizeBytes = outputSize, PercentageSaved = saved, ValidatedUtc = DateTimeOffset.UtcNow };
    }
}
