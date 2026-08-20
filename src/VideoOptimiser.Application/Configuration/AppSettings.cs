using YamlDotNet.Serialization;

namespace VideoOptimiser.Application.Configuration;

[YamlSerializable]
public sealed class AppSettings
{
    [YamlMember(Alias = "version")] public int Version { get; set; } = 1;
    [YamlMember(Alias = "tools")] public ToolSettings Tools { get; set; } = new();
    [YamlMember(Alias = "processing")] public ProcessingSettings Processing { get; set; } = new();
    [YamlMember(Alias = "database")] public DatabaseSettings Database { get; set; } = new();
    [YamlMember(Alias = "watch")] public WatchSettings Watch { get; set; } = new();
    [YamlMember(Alias = "eligibility")] public EligibilitySettings Eligibility { get; set; } = new();
    [YamlMember(Alias = "quality")] public QualitySettings Quality { get; set; } = new();
    [YamlMember(Alias = "savings")] public SavingsSettings Savings { get; set; } = new();
    [YamlMember(Alias = "original")] public OriginalSettings Original { get; set; } = new();
    [YamlMember(Alias = "validation")] public ValidationSettings Validation { get; set; } = new();
}

[YamlSerializable]
public sealed class ToolSettings
{
    [YamlMember(Alias = "abAv1Path")] public string AbAv1Path { get; set; } = "ab-av1";
    [YamlMember(Alias = "ffmpegPath")] public string FfmpegPath { get; set; } = "ffmpeg";
    [YamlMember(Alias = "ffprobePath")] public string FfprobePath { get; set; } = "ffprobe";
    [YamlMember(Alias = "sshPath")] public string SshPath { get; set; } = "ssh";
    [YamlMember(Alias = "sftpPath")] public string SftpPath { get; set; } = "sftp";
}

[YamlSerializable]
public sealed class ProcessingSettings
{
    [YamlMember(Alias = "mode")] public string Mode { get; set; } = ProcessingModes.Local;
    [YamlMember(Alias = "remoteSsh")] public RemoteSshSettings RemoteSsh { get; set; } = new();
}

[YamlSerializable]
public sealed class RemoteSshSettings
{
    [YamlMember(Alias = "host")] public string Host { get; set; } = string.Empty;
    [YamlMember(Alias = "user")] public string User { get; set; } = string.Empty;
    [YamlMember(Alias = "identityFile")] public string IdentityFile { get; set; } = string.Empty;
    [YamlMember(Alias = "knownHostsFile")] public string KnownHostsFile { get; set; } = string.Empty;
    [YamlMember(Alias = "lifecycle")] public string Lifecycle { get; set; } = RemoteLifecycleModes.Manual;
    [YamlMember(Alias = "workingDirectory")] public string WorkingDirectory { get; set; } = "/var/tmp/video-optimiser";
    [YamlMember(Alias = "minimumCpuCount")] public int MinimumCpuCount { get; set; } = 8;
    [YamlMember(Alias = "minimumAvailableMemory")] public string MinimumAvailableMemory { get; set; } = "14GiB";
    [YamlMember(Alias = "minimumFreeDiskMultiplier")] public double MinimumFreeDiskMultiplier { get; set; } = 2.5;
    [YamlMember(Alias = "hetzner")] public HetznerSettings Hetzner { get; set; } = new();
}

[YamlSerializable]
public sealed class HetznerSettings
{
    [YamlMember(Alias = "apiTokenEnvironmentVariable")] public string ApiTokenEnvironmentVariable { get; set; } = "VIDEO_OPTIMISER_HETZNER_TOKEN";
    [YamlMember(Alias = "apiTokenFile")] public string ApiTokenFile { get; set; } = ".env";
    [YamlMember(Alias = "serverType")] public string ServerType { get; set; } = "cx43";
    [YamlMember(Alias = "image")] public string Image { get; set; } = "ubuntu-24.04";
    [YamlMember(Alias = "location")] public string Location { get; set; } = "hel1";
    [YamlMember(Alias = "sshKeyName")] public string SshKeyName { get; set; } = string.Empty;
    [YamlMember(Alias = "serverNamePrefix")] public string ServerNamePrefix { get; set; } = "video-optimiser";
    [YamlMember(Alias = "bootstrapScriptPath")] public string BootstrapScriptPath { get; set; } = "scripts/bootstrap-remote-worker.sh";
    [YamlMember(Alias = "bootstrapTimeout")] public string BootstrapTimeout { get; set; } = "90m";
    [YamlMember(Alias = "deleteAfterRun")] public bool DeleteAfterRun { get; set; } = true;
}

public static class RemoteLifecycleModes
{
    public const string Manual = "manual";
    public const string Hetzner = "hetzner";
}

public static class RemoteExecutionIdentity
{
    public static string Host(RemoteSshSettings settings) =>
        settings.Lifecycle.Equals(RemoteLifecycleModes.Hetzner, StringComparison.OrdinalIgnoreCase)
            ? $"hetzner:{settings.Hetzner.ServerType}:{settings.Hetzner.Location}"
            : settings.Host;
}

public static class ProcessingModes
{
    public const string Local = "local";
    public const string RemoteSsh = "remoteSsh";
}

[YamlSerializable]
public sealed class DatabaseSettings
{
    [YamlMember(Alias = "path")] public string Path { get; set; } = "jobs.db";
}

[YamlSerializable]
public sealed class WatchSettings
{
    [YamlMember(Alias = "roots")] public List<WatchRootSettings> Roots { get; set; } = [];
}

[YamlSerializable]
public sealed class WatchRootSettings
{
    [YamlMember(Alias = "path")] public string Path { get; set; } = string.Empty;
    [YamlMember(Alias = "recursive")] public bool Recursive { get; set; } = true;
}

[YamlSerializable]
public sealed class EligibilitySettings
{
    [YamlMember(Alias = "extensions")] public List<string> Extensions { get; set; } = [".mkv", ".mp4", ".mov", ".m4v"];
    [YamlMember(Alias = "rules")] public List<EligibilityRuleSettings> Rules { get; set; } = [];
    [YamlMember(Alias = "excludedExtensions")] public List<string> ExcludedExtensions { get; set; } = [".tmp", ".part", ".partial"];
    [YamlMember(Alias = "excludedNamePatterns")] public List<string> ExcludedNamePatterns { get; set; } = ["*.encoding.*", "*.crf-search.*", "*.video-optimiser.*"];
    [YamlMember(Alias = "excludedDirectories")] public List<string> ExcludedDirectories { get; set; } = [".video-optimiser", "Archive"];
    [YamlMember(Alias = "ignoreHiddenFiles")] public bool IgnoreHiddenFiles { get; set; } = true;
    [YamlMember(Alias = "ignoreSystemFiles")] public bool IgnoreSystemFiles { get; set; } = true;
}

[YamlSerializable]
public sealed class EligibilityRuleSettings
{
    [YamlMember(Alias = "codecs")] public List<string> Codecs { get; set; } = [];
    [YamlMember(Alias = "resolution")] public string Resolution { get; set; } = string.Empty;
    [YamlMember(Alias = "minimumVideoBitrate")] public string MinimumVideoBitrate { get; set; } = string.Empty;
    [YamlMember(Alias = "minimumFileSize")] public string MinimumFileSize { get; set; } = string.Empty;
}

[YamlSerializable]
public sealed class QualitySettings
{
    [YamlMember(Alias = "minimumVmaf")] public double MinimumVmaf { get; set; } = 95;
    [YamlMember(Alias = "preset")] public int Preset { get; set; } = 6;
    [YamlMember(Alias = "encoder")] public string Encoder { get; set; } = "libsvtav1";
    [YamlMember(Alias = "pixelFormat")] public string PixelFormat { get; set; } = "yuv420p10le";
    [YamlMember(Alias = "crfSearch")] public CrfSearchSettings CrfSearch { get; set; } = new();
}

[YamlSerializable]
public sealed class CrfSearchSettings
{
    [YamlMember(Alias = "enabled")] public bool Enabled { get; set; } = true;
    [YamlMember(Alias = "minCrf")] public int MinCrf { get; set; } = 18;
    [YamlMember(Alias = "maxCrf")] public int MaxCrf { get; set; } = 50;
    [YamlMember(Alias = "sampleCount")] public int SampleCount { get; set; } = 5;
    [YamlMember(Alias = "sampleDuration")] public string SampleDuration { get; set; } = "20s";
}

[YamlSerializable]
public sealed class SavingsSettings
{
    [YamlMember(Alias = "requireSmallerOutput")] public bool RequireSmallerOutput { get; set; } = true;
    [YamlMember(Alias = "minimumBytesSaved")] public string MinimumBytesSaved { get; set; } = "100MiB";
    [YamlMember(Alias = "minimumPercentageSaved")] public decimal MinimumPercentageSaved { get; set; } = 5;
}

[YamlSerializable]
public sealed class OriginalSettings
{
    [YamlMember(Alias = "action")] public string Action { get; set; } = "delete";
}

[YamlSerializable]
public sealed class ValidationSettings
{
    [YamlMember(Alias = "runFfprobe")] public bool RunFfprobe { get; set; } = true;
    [YamlMember(Alias = "decodeTest")] public bool DecodeTest { get; set; } = true;
    [YamlMember(Alias = "decodeTestMode")] public string DecodeTestMode { get; set; } = "sampled";
    [YamlMember(Alias = "requireExpectedVideoCodec")] public bool RequireExpectedVideoCodec { get; set; } = true;
    [YamlMember(Alias = "requireDurationTolerance")] public bool RequireDurationTolerance { get; set; } = true;
    [YamlMember(Alias = "durationToleranceSeconds")] public double DurationToleranceSeconds { get; set; } = 1;
    [YamlMember(Alias = "requireStreamParity")] public bool RequireStreamParity { get; set; } = true;
    [YamlMember(Alias = "requireNonZeroLength")] public bool RequireNonZeroLength { get; set; } = true;
}
