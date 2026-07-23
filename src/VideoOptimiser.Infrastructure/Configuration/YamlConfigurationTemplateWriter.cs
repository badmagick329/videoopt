using System.Text;
using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Infrastructure.Configuration;

public sealed class YamlConfigurationTemplateWriter(IConfigurationLoader configurationLoader) : IConfigurationTemplateWriter
{
    public async Task<string> WriteAsync(string? explicitPath, CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(explicitPath ?? configurationLoader.GetDefaultConfigurationPath());
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        try
        {
            await using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteAsync(Template.AsMemory(), cancellationToken);
        }
        catch (IOException) when (File.Exists(destination))
        {
            throw new InvalidOperationException($"Refusing to overwrite the existing configuration file '{destination}'.");
        }

        return destination;
    }

    private const string Template = """
# VideoOptimiser configuration
# Edit watch.roots, then run doctor.
version: 1

tools:
  # Command names or paths for the required media tools.
  abAv1Path: "ab-av1"
  ffmpegPath: "ffmpeg"
  ffprobePath: "ffprobe"

database:
  # SQLite job database. Relative paths are relative to this YAML file.
  path: "jobs.db"

watch:
  # Folders searched by queue discover. Add at least one.
  roots:
    # - path: "D:\\Videos"
    #   recursive: true

eligibility:
  # File extensions considered during discovery.
  extensions: [".mkv", ".mp4", ".mov", ".m4v"]
  # A file is queued when it matches any one rule. All criteria in a rule must match.
  rules:
    - codecs: ["h264"]
      resolution: "4k+"
      minimumVideoBitrate: "20Mbps"
      minimumFileSize: "2GiB"
    - codecs: ["h264"]
      resolution: "1080p-1440p"
      minimumVideoBitrate: "8Mbps"
      minimumFileSize: "800MiB"
  # Files with these extensions are always skipped.
  excludedExtensions: [".tmp", ".part", ".partial"]
  # Filename patterns for temporary or tool-created files to skip.
  excludedNamePatterns: ["*.encoding.*", "*.crf-search.*", "*.video-optimiser.*"]
  # Directory names to skip anywhere beneath a watch root.
  excludedDirectories: [".video-optimiser", "Archive"]
  # Skip Windows hidden files.
  ignoreHiddenFiles: true
  # Skip Windows system files.
  ignoreSystemFiles: true

quality:
  # Minimum visual quality target used by CRF search.
  minimumVmaf: 95
  # SVT-AV1 preset; lower is slower and generally more efficient.
  preset: 6
  # AV1 encoder passed to ab-av1.
  encoder: "libsvtav1"
  # Output pixel format.
  pixelFormat: "yuv420p10le"
  crfSearch:
    # Whether CRF search runs before encoding.
    enabled: true
    # Inclusive CRF search range.
    minCrf: 18
    maxCrf: 50
    # Maximum samples; short videos automatically use fewer.
    sampleCount: 5
    # Duration of each CRF-search sample.
    sampleDuration: "20s"

savings:
  # Reject output that is not smaller than the source.
  requireSmallerOutput: true
  # Minimum absolute saving required for validation.
  minimumBytesSaved: "100MiB"
  # Minimum percentage saving required for validation.
  minimumPercentageSaved: 5

original:
  # The only currently supported finalisation policy.
  action: "delete"

validation:
  # Probe source and output stream metadata with ffprobe.
  runFfprobe: true
  # Decode samples with ffmpeg after encoding.
  decodeTest: true
  # Decode test scope: none, sampled, or full.
  decodeTestMode: "sampled"
  # Require an AV1 primary video stream.
  requireExpectedVideoCodec: true
  # Require source/output durations to be within the tolerance.
  requireDurationTolerance: true
  # Allowed source/output duration difference.
  durationToleranceSeconds: 1
  # Require matching audio and subtitle stream counts.
  requireStreamParity: true
  # Reject empty output files.
  requireNonZeroLength: true
""";
}
