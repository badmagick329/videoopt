using System.Text;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Config;

namespace VideoOptimiser.Infrastructure.Features.Config;

public sealed class YamlConfigurationTemplateWriter(IConfigurationLoader configurationLoader) : IConfigurationTemplateWriter
{
    public string GetDestination(string? explicitPath) => Path.GetFullPath(explicitPath ?? configurationLoader.GetDefaultConfigurationPath());

    public async Task<string> WriteAsync(string? explicitPath, QualityLevel quality, CancellationToken cancellationToken = default)
    {
        var destination = GetDestination(explicitPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var contents = Template
            .Replace("{{QUALITY_LEVEL}}", QualityLevels.TemplateComment(quality), StringComparison.Ordinal)
            .Replace("{{MINIMUM_VMAF}}", QualityLevels.MinimumVmaf(quality).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        try
        {
            await using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteAsync(contents.AsMemory(), cancellationToken);
        }
        catch (IOException) when (File.Exists(destination))
        {
            throw IConfigurationTemplateWriter.RefuseOverwrite(destination);
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
  sshPath: "ssh"
  sftpPath: "sftp"

processing:
  # Run CRF search and encoding locally or over SSH.
  mode: "local"
  remoteSsh:
    # manual uses this OpenSSH host/config alias. hetzner fills it at runtime.
    lifecycle: "manual"
    host: ""
    # Leave these empty when the manual SSH alias already supplies them.
    user: ""
    identityFile: ""
    knownHostsFile: ""
    workingDirectory: "/var/tmp/video-optimiser"
    minimumCpuCount: 8
    minimumAvailableMemory: "14GiB"
    # Remote free space must be at least this multiple of each source file.
    minimumFreeDiskMultiplier: 2.5
    hetzner:
      # Used only when lifecycle is hetzner. An existing environment variable takes precedence.
      apiTokenEnvironmentVariable: "VIDEO_OPTIMISER_HETZNER_TOKEN"
      # Loaded automatically when the environment variable is not set. Keep this file ignored.
      apiTokenFile: ".env"
      # Tried in this order, each in every location below. Each needs x86, minimumCpuCount cores and minimumAvailableMemory.
      serverTypes: ["cx43", "cx53", "cpx42", "ccx33"]
      # Skips any type and location costing more than this, in EUR per hour including VAT. null removes the cap.
      maxHourlyPrice: 0.30
      image: "ubuntu-24.04"
      # Candidates are tried in this order after the availability preflight.
      locations: ["hel1", "fsn1", "nbg1"]
      sshKeyName: ""
      serverNamePrefix: "video-optimiser"
      bootstrapScriptPath: "scripts/bootstrap-remote-worker.sh"
      bootstrapTimeout: "90m"
      # Delete after a clean run. Interrupted or unreachable work is retained.
      deleteAfterRun: true

database:
  # SQLite job database. Relative paths are relative to this YAML file.
  path: "jobs.db"

watch:
  # Folders searched by queue add. Add at least one.
  roots:
    # - path: "D:\\Videos"
    #   recursive: true

immich:
  # Take originals from an Immich library as well. Leave baseUrl empty to not use Immich.
  # Use the LAN address: a public hostname behind Cloudflare rejects uploads over 100 MB.
  baseUrl: ""
  # The API key loads from this environment variable, then from apiKeyFile. Never put it in this file.
  # Create it in Immich under Account settings > API keys with these permissions: asset.read,
  # asset.download, asset.upload, asset.update, asset.copy, asset.delete, tag.asset, user.read.
  apiKeyEnvironmentVariable: "VIDEO_OPTIMISER_IMMICH_API_KEY"
  apiKeyFile: ".env"
  # Only videos taken more than this many days ago are converted. Keep it above the phone's
  # "Free up space" cutoff, so each original has left the phone first.
  minimumAgeDays: 90
  # Skip favourites; the phone's "Free up space" keeps them by default.
  skipFavorites: true
  # Downloaded originals and their temporary outputs. Relative paths are relative to this YAML file.
  stagingDirectory: "immich-staging"

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
  # {{QUALITY_LEVEL}}
  # Minimum visual quality target used by CRF search.
  minimumVmaf: {{MINIMUM_VMAF}}
  # Largest share of the original size the AV1 output may keep. A video that cannot shrink this far at the
  # minimum VMAF is skipped as not worth converting, and checked again when the quality settings change.
  maxEncodedPercent: 80
  # SVT-AV1 preset; lower is slower and generally more efficient.
  preset: 6
  # AV1 encoder passed to ab-av1.
  encoder: "libsvtav1"
  # Output pixel format.
  pixelFormat: "yuv420p10le"
  crfSearch:
    # Inclusive CRF search range.
    minCrf: 18
    maxCrf: 50
    # Maximum samples; short videos automatically use fewer.
    sampleCount: 5
    # Duration of each CRF-search sample.
    sampleDuration: "20s"

savings:
  # Reject output that is not smaller than the original.
  requireSmallerOutput: true
  # Minimum saving per video. A video too small to save this much is never searched, and the
  # CRF search rejects any CRF that would save less. The encode is checked against it again.
  minimumBytesSaved: "100MiB"

original:
  # What finalize does with the original: delete, archive (move it to archiveDirectory),
  # or keep (leave it and install the AV1 beside it as <name>.av1<extension>).
  action: "delete"
  # A folder name is created beside each original; an absolute path collects them all.
  # Discovery never scans it.
  archiveDirectory: "Archive"

validation:
  # Codec, duration, stream-count and savings checks always run.
  # Decode the output with ffmpeg before it can replace the original:
  # sampled (start, middle and end), full, or none.
  decodeTest: "sampled"
  # Allowed original/output duration difference.
  durationToleranceSeconds: 1
""";
}
