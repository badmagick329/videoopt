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
# VideoOptimiser configuration (Phase 1 template)
# Add at least one watch root and choose an archive directory before running doctor.
version: 1

tools:
  abAv1Path: "ab-av1"
  ffmpegPath: "ffmpeg"
  ffprobePath: "ffprobe"

database:
  path: "jobs.db"

logging:
  level: "Information"
  directory: "logs"
  retainDays: 30
  console: true
  structuredFile: true

watch:
  roots: []
  reconciliationInterval: "10m"

processing:
  minimumFileSize: "2GiB"
  maximumConcurrentJobs: 1
  retryCount: 2
  retryDelay: "10m"
  resumeInterruptedJobs: true
  preventSystemSleep: true

quality:
  minimumVmaf: 95
  preset: 6
  encoder: "libsvtav1"
  pixelFormat: "yuv420p10le"

output:
  container: "preserve"
  temporaryDirectory: ""
  temporarySuffix: ".video-optimiser.encoding"
  preserveTimestamps: true
  preserveMetadata: true
  preserveChapters: true
  preserveAttachments: true
  copySubtitles: true
  copyAudio: true

savings:
  requireSmallerOutput: true
  minimumBytesSaved: "100MiB"
  minimumPercentageSaved: 5

original:
  action: "archive"
  archiveDirectory: ""
  preserveRelativePath: true
  collisionStrategy: "timestamp"

validation:
  runFfprobe: true
  decodeTest: true
  decodeTestMode: "sampled"
  requireExpectedVideoCodec: true
  requireDurationTolerance: true
  durationToleranceSeconds: 1
  requireStreamParity: true
  requireNonZeroLength: true
""";
}
