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

/// <summary>
/// What validation decided about a finished AV1 (VOPT-116). Only <see cref="Failed"/> leaves nothing to review; a check the
/// user can judge by looking at the video warns instead, because every hard failure the user hit on phone video was a false
/// alarm that threw away a finished encode.
/// </summary>
public enum ValidationVerdict
{
    Passed,
    /// <summary>May be finalised, after the user has looked at what the warnings name.</summary>
    Warned,
    /// <summary>The AV1 is whole but saves too little to keep: the job ends skipped and the AV1 is deleted.</summary>
    NotWorthConverting,
    /// <summary>Nothing to review: a file is missing or empty, or the AV1 is unreadable or not AV1.</summary>
    Failed
}

public sealed class ValidationReport
{
    /// <summary>May be finalised: passed, with or without warnings. Finalisation reads this alone.</summary>
    public bool Passed { get; set; }
    /// <summary>Hard failures only.</summary>
    public List<string> Failures { get; set; } = [];
    /// <summary>Doubtful checks for the user to judge. A record written before VOPT-117 has none, and reads as an empty list.</summary>
    public List<string> Warnings { get; set; } = [];
    /// <summary>Why the AV1 is not worth keeping, naming the savings setting; null when it is.</summary>
    public string? NotWorthConverting { get; set; }

    /// <summary>Hard failure wins over not worth converting, which wins over warnings. A record from before VOPT-117 that did not pass reads as failed.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ValidationVerdict Verdict =>
        Failures.Count > 0 || !Passed && NotWorthConverting is null ? ValidationVerdict.Failed
        : NotWorthConverting is not null ? ValidationVerdict.NotWorthConverting
        : Warnings.Count > 0 ? ValidationVerdict.Warned
        : ValidationVerdict.Passed;

    public long SourceSizeBytes { get; set; }
    public long OutputSizeBytes { get; set; }
    public decimal PercentageSaved { get; set; }
    public DateTimeOffset ValidatedUtc { get; set; }

    /// <summary>
    /// The version of validation's rules that wrote this report; bump it when a change to the checks could turn a failure
    /// into a pass, so the next run checks failed videos again (VOPT-119). No initializer: a record written before this
    /// change must read as 0, not as the current version.
    /// </summary>
    public int RulesVersion { get; set; }

    public const int CurrentRulesVersion = 1;
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
