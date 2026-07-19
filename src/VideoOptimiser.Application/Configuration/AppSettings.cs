namespace VideoOptimiser.Application.Configuration;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public ToolSettings Tools { get; set; } = new();
    public DatabaseSettings Database { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public WatchSettings Watch { get; set; } = new();
    public ProcessingSettings Processing { get; set; } = new();
    public QualitySettings Quality { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
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
    public string ReconciliationInterval { get; set; } = "10m";
}

public sealed class WatchRootSettings
{
    public string Path { get; set; } = string.Empty;
    public bool Recursive { get; set; } = true;
}

public sealed class ProcessingSettings
{
    public string MinimumFileSize { get; set; } = "2GiB";
    public int MaximumConcurrentJobs { get; set; } = 1;
    public int RetryCount { get; set; } = 2;
    public string RetryDelay { get; set; } = "10m";
    public bool ResumeInterruptedJobs { get; set; } = true;
    public bool PreventSystemSleep { get; set; } = true;
}

public sealed class QualitySettings
{
    public double MinimumVmaf { get; set; } = 95;
    public int Preset { get; set; } = 6;
    public string Encoder { get; set; } = "libsvtav1";
    public string PixelFormat { get; set; } = "yuv420p10le";
}

public sealed class OutputSettings
{
    public string Container { get; set; } = "preserve";
    public string TemporaryDirectory { get; set; } = string.Empty;
    public string TemporarySuffix { get; set; } = ".video-optimiser.encoding";
    public bool PreserveTimestamps { get; set; } = true;
    public bool PreserveMetadata { get; set; } = true;
    public bool PreserveChapters { get; set; } = true;
    public bool PreserveAttachments { get; set; } = true;
    public bool CopySubtitles { get; set; } = true;
    public bool CopyAudio { get; set; } = true;
}

public sealed class SavingsSettings
{
    public bool RequireSmallerOutput { get; set; } = true;
    public string MinimumBytesSaved { get; set; } = "100MiB";
    public decimal MinimumPercentageSaved { get; set; } = 5;
}

public sealed class OriginalSettings
{
    public string Action { get; set; } = "archive";
    public string ArchiveDirectory { get; set; } = string.Empty;
    public bool PreserveRelativePath { get; set; } = true;
    public string CollisionStrategy { get; set; } = "timestamp";
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
