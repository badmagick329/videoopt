namespace VideoOptimiser.Application.Configuration;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public ToolSettings Tools { get; set; } = new();
    public DatabaseSettings Database { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public WatchSettings Watch { get; set; } = new();
    public EligibilitySettings Eligibility { get; set; } = new();
    public QualitySettings Quality { get; set; } = new();
    public SavingsSettings Savings { get; set; } = new();
    public OriginalSettings Original { get; set; } = new();
    public ValidationSettings Validation { get; set; } = new();
}

public sealed class ToolSettings
{
    public string AbAv1Path { get; set; } = "ab-av1";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
}

public sealed class DatabaseSettings
{
    public string Path { get; set; } = "jobs.db";
}

public sealed class LoggingSettings
{
    public string Level { get; set; } = "Information";
    public string Directory { get; set; } = "logs";
    public int RetainDays { get; set; } = 30;
    public bool Console { get; set; } = true;
    public bool StructuredFile { get; set; } = true;
}

public sealed class WatchSettings
{
    public List<WatchRootSettings> Roots { get; set; } = [];
}

public sealed class WatchRootSettings
{
    public string Path { get; set; } = string.Empty;
    public bool Recursive { get; set; } = true;
}

public sealed class EligibilitySettings
{
    public List<string> Extensions { get; set; } = [".mkv", ".mp4", ".mov", ".m4v"];
    public List<EligibilityRuleSettings> Rules { get; set; } = [];
    public List<string> ExcludedExtensions { get; set; } = [".tmp", ".part", ".partial"];
    public List<string> ExcludedNamePatterns { get; set; } = ["*.encoding.*", "*.crf-search.*", "*.video-optimiser.*"];
    public List<string> ExcludedDirectories { get; set; } = [".video-optimiser", "Archive"];
    public bool IgnoreHiddenFiles { get; set; } = true;
    public bool IgnoreSystemFiles { get; set; } = true;
}

public sealed class EligibilityRuleSettings
{
    public List<string> Codecs { get; set; } = [];
    public string Resolution { get; set; } = string.Empty;
    public string MinimumVideoBitrate { get; set; } = string.Empty;
    public string MinimumFileSize { get; set; } = string.Empty;
}

public sealed class QualitySettings
{
    public double MinimumVmaf { get; set; } = 95;
    public int Preset { get; set; } = 6;
    public string Encoder { get; set; } = "libsvtav1";
    public string PixelFormat { get; set; } = "yuv420p10le";
    public CrfSearchSettings CrfSearch { get; set; } = new();
}

public sealed class CrfSearchSettings
{
    public bool Enabled { get; set; } = true;
    public int MinCrf { get; set; } = 18;
    public int MaxCrf { get; set; } = 50;
    public int SampleCount { get; set; } = 5;
    public string SampleDuration { get; set; } = "20s";
}

public sealed class SavingsSettings
{
    public bool RequireSmallerOutput { get; set; } = true;
    public string MinimumBytesSaved { get; set; } = "100MiB";
    public decimal MinimumPercentageSaved { get; set; } = 5;
}

public sealed class OriginalSettings
{
    public string Action { get; set; } = "delete";
}

public sealed class ValidationSettings
{
    public bool RunFfprobe { get; set; } = true;
    public bool DecodeTest { get; set; } = true;
    public string DecodeTestMode { get; set; } = "sampled";
    public bool RequireExpectedVideoCodec { get; set; } = true;
    public bool RequireDurationTolerance { get; set; } = true;
    public double DurationToleranceSeconds { get; set; } = 1;
    public bool RequireStreamParity { get; set; } = true;
    public bool RequireNonZeroLength { get; set; } = true;
}
