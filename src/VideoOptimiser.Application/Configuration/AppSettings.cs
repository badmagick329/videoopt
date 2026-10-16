using YamlDotNet.Serialization;
using VideoOptimiser.Application.Eligibility;

namespace VideoOptimiser.Application.Configuration;

[YamlSerializable]
public sealed class AppSettings
{
    [YamlMember(Alias = "version")] public int Version { get; set; } = 1;
    [YamlMember(Alias = "tools")] public ToolSettings Tools { get; set; } = new();
    [YamlMember(Alias = "processing")] public ProcessingSettings Processing { get; set; } = new();
    [YamlMember(Alias = "database")] public DatabaseSettings Database { get; set; } = new();
    [YamlMember(Alias = "watch")] public WatchSettings Watch { get; set; } = new();
    [YamlMember(Alias = "immich")] public ImmichSettings Immich { get; set; } = new();
    [YamlMember(Alias = "eligibility")] public EligibilitySettings Eligibility { get; set; } = new();
    [YamlMember(Alias = "quality")] public QualitySettings Quality { get; set; } = new();
    [YamlMember(Alias = "savings")] public SavingsSettings Savings { get; set; } = new();
    [YamlMember(Alias = "original")] public OriginalSettings Original { get; set; } = new();
    [YamlMember(Alias = "validation")] public ValidationSettings Validation { get; set; } = new();

    /// <summary>
    /// A copy whose <c>processing.mode</c> is <paramref name="mode"/>, sharing every other section, so a host the lifecycle
    /// finds is written into these settings too. For a caller that must act in one mode without changing what the rest of the
    /// process reads: the full-screen view manages the Hetzner worker in any mode, as the worker commands do.
    /// </summary>
    public AppSettings WithMode(string mode)
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Processing = new ProcessingSettings { Mode = mode, RemoteSsh = Processing.RemoteSsh };
        return copy;
    }
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
    // Tried in order, each across every location, so a sold-out type falls through to the next.
    [YamlMember(Alias = "serverTypes")] public List<string> ServerTypes { get; set; } = ["cx43", "cx53", "cpx42", "ccx33"];
    // Gross EUR per hour, as Hetzner bills it. Null means no cap.
    [YamlMember(Alias = "maxHourlyPrice")] public decimal? MaxHourlyPrice { get; set; } = 0.30m;
    [YamlMember(Alias = "image")] public string Image { get; set; } = "ubuntu-24.04";
    [YamlMember(Alias = "locations")] public List<string> Locations { get; set; } = ["hel1", "fsn1", "nbg1"];
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
    /// <summary>
    /// The binding for every managed Hetzner job. The server type and location vary per run with the
    /// fallback list, and a started job lives on whichever retained worker exists, so neither belongs in it.
    /// The colon keeps it apart from a manual SSH alias named "hetzner".
    /// </summary>
    public const string ManagedHetzner = "hetzner:managed";

    // Before serverTypes, the binding was hetzner:{serverType}:{locations}; jobs started then must still resume.
    private const string LegacyManagedHetznerPrefix = "hetzner:";

    public static string Host(RemoteSshSettings settings) =>
        settings.Lifecycle.Equals(RemoteLifecycleModes.Hetzner, StringComparison.OrdinalIgnoreCase)
            ? ManagedHetzner
            : settings.Host;

    public static bool Matches(string? stored, string? expected) =>
        string.Equals(stored, expected, StringComparison.Ordinal) ||
        (expected == ManagedHetzner && IsManagedHetzner(stored));

    public static bool IsManagedHetzner(string? stored) =>
        stored is not null && stored.StartsWith(LegacyManagedHetznerPrefix, StringComparison.Ordinal);
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

/// <summary>
/// The Immich source. An empty <see cref="BaseUrl"/> means Immich is not used. The API key never lives here: it loads from
/// <see cref="ApiKeyEnvironmentVariable"/>, then from <see cref="ApiKeyFile"/>, so it stays out of YAML, logs and the database.
/// </summary>
[YamlSerializable]
public sealed class ImmichSettings
{
    [YamlMember(Alias = "baseUrl")] public string BaseUrl { get; set; } = string.Empty;
    [YamlMember(Alias = "apiKeyEnvironmentVariable")] public string ApiKeyEnvironmentVariable { get; set; } = "VIDEO_OPTIMISER_IMMICH_API_KEY";
    [YamlMember(Alias = "apiKeyFile")] public string ApiKeyFile { get; set; } = ".env";

    /// <summary>
    /// The age gate. It must stay above the phone's "Free up space" cutoff, so an original has left the phone before it is
    /// converted; otherwise the phone would upload it again once Immich purges the trashed original.
    /// </summary>
    [YamlMember(Alias = "minimumAgeDays")] public int MinimumAgeDays { get; set; } = 90;

    [YamlMember(Alias = "skipFavorites")] public bool SkipFavorites { get; set; } = true;
    [YamlMember(Alias = "stagingDirectory")] public string StagingDirectory { get; set; } = "immich-staging";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
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
    // ab-av1 rejects a CRF whose output would exceed this share of the original, so a video that cannot shrink this far is skipped rather than converted.
    [YamlMember(Alias = "maxEncodedPercent")] public int MaxEncodedPercent { get; set; } = 80;
    [YamlMember(Alias = "preset")] public int Preset { get; set; } = 6;
    [YamlMember(Alias = "encoder")] public string Encoder { get; set; } = "libsvtav1";
    [YamlMember(Alias = "pixelFormat")] public string PixelFormat { get; set; } = "yuv420p10le";
    [YamlMember(Alias = "crfSearch")] public CrfSearchSettings CrfSearch { get; set; } = new();

    /// <summary>A copy with the sample count the search will use. Cloned rather than rebuilt field by field, so a new setting cannot be dropped.</summary>
    public QualitySettings WithSampleCount(int sampleCount)
    {
        var copy = (QualitySettings)MemberwiseClone();
        copy.CrfSearch = CrfSearch.WithSampleCount(sampleCount);
        return copy;
    }

    /// <summary>
    /// The video's own size cap, set on a job's copy only. It travels with the settings because every reader of
    /// <see cref="MaxEncodedPercent"/> (ab-av1's arguments, local or remote, the progress verdicts and the skip reason) must
    /// use the cap, and the skip reason must name the setting that set it.
    /// </summary>
    [YamlIgnore] public SizeCap? SizeCap { get; private set; }

    public QualitySettings WithSizeCap(SizeCap cap)
    {
        var copy = (QualitySettings)MemberwiseClone();
        copy.MaxEncodedPercent = cap.Percent;
        copy.SizeCap = cap;
        return copy;
    }
}

[YamlSerializable]
public sealed class CrfSearchSettings
{
    [YamlMember(Alias = "minCrf")] public int MinCrf { get; set; } = 18;
    [YamlMember(Alias = "maxCrf")] public int MaxCrf { get; set; } = 50;
    [YamlMember(Alias = "sampleCount")] public int SampleCount { get; set; } = 5;
    [YamlMember(Alias = "sampleDuration")] public string SampleDuration { get; set; } = "20s";

    public CrfSearchSettings WithSampleCount(int sampleCount)
    {
        var copy = (CrfSearchSettings)MemberwiseClone();
        copy.SampleCount = sampleCount;
        return copy;
    }
}

[YamlSerializable]
public sealed class SavingsSettings
{
    [YamlMember(Alias = "requireSmallerOutput")] public bool RequireSmallerOutput { get; set; } = true;
    [YamlMember(Alias = "minimumBytesSaved")] public string MinimumBytesSaved { get; set; } = "100MiB";
}

[YamlSerializable]
public sealed class OriginalSettings
{
    [YamlMember(Alias = "action")] public string Action { get; set; } = OriginalActions.Delete;

    /// <summary>
    /// Where <c>archive</c> moves originals. A bare folder name is created next to each original, so archives stay on the
    /// original's drive and the move is a rename; an absolute path collects every original in one folder.
    /// </summary>
    [YamlMember(Alias = "archiveDirectory")] public string ArchiveDirectory { get; set; } = "Archive";
}

public static class OriginalActions
{
    public const string Delete = "delete";
    public const string Archive = "archive";
    public const string Keep = "keep";
}

[YamlSerializable]
public sealed class ValidationSettings
{
    [YamlMember(Alias = "decodeTest")] public string DecodeTest { get; set; } = DecodeTestModes.Sampled;
    [YamlMember(Alias = "durationToleranceSeconds")] public double DurationToleranceSeconds { get; set; } = 1;
}

public static class DecodeTestModes
{
    public const string None = "none";
    public const string Sampled = "sampled";
    public const string Full = "full";
}
