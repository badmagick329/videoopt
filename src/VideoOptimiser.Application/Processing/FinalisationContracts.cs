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
