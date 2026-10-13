using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Processing;

public sealed class OutputManifest
{
    public string SourcePath { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public int Crf { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public ValidationReport? Validation { get; set; }
}

public sealed class ValidationReport
{
    public bool Passed { get; set; }
    public List<string> Failures { get; set; } = [];
    public long SourceSizeBytes { get; set; }
    public long OutputSizeBytes { get; set; }
    public decimal PercentageSaved { get; set; }
    public DateTimeOffset ValidatedUtc { get; set; }
}

public interface IOutputManifestStore
{
    string GetPath(string outputPath);
    Task SaveAsync(OutputManifest manifest, CancellationToken cancellationToken = default);
    Task<OutputManifest> LoadAsync(string outputPath, CancellationToken cancellationToken = default);
}

public interface IFileFingerprintService
{
    Task<string> CreateAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where finalisation put the AV1, and where <c>archive</c> moved the original. <paramref name="Warning"/> is set when the AV1
/// is installed but something minor failed afterwards, such as copying the original's file dates.
/// </summary>
public sealed record FileInstallation(string InstalledPath, string? ArchivedOriginalPath, string? Warning = null);

public interface ISafeFileInstaller
{
    Task<FileInstallation> InstallAsync(string sourcePath, string outputPath, OriginalSettings original, CancellationToken cancellationToken = default);
}
