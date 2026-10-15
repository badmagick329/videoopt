namespace VideoOptimiser.Application.TemporaryOutputs;

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

public static class OutputManifestStoreExtensions
{
    /// <summary>
    /// The record, or null when it is missing, unreadable or corrupt. Finalize and validate treat all three alike: the record
    /// is the only proof the original is unchanged, so neither rebuilds it from the job.
    /// </summary>
    public static async Task<OutputManifest?> TryLoadAsync(this IOutputManifestStore store, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.LoadAsync(outputPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

public interface IFileFingerprintService
{
    Task<string> CreateAsync(string path, CancellationToken cancellationToken = default);
}
