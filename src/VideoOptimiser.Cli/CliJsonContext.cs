using System.Text.Json.Serialization;

namespace VideoOptimiser.Cli;

internal sealed record DoctorJsonOutput(string ConfigurationPath, int ExitCode, DoctorJsonDiagnostic[] Diagnostics);
internal sealed record DoctorJsonDiagnostic(string Category, string Status, string Code, string Message);
internal sealed record JobJsonOutput(string Id, string Status, string SourcePath, int? Crf, string? OutputPath, decimal? PercentageSaved, string? FailureCategory, string? FailureMessage, DateTimeOffset UpdatedUtc);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(DoctorJsonOutput))]
[JsonSerializable(typeof(JobJsonOutput[]))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
