using System.Text.Json.Serialization;

namespace VideoOptimiser.Cli.Features.Queue;

internal sealed record JobJsonOutput(string Id, string Status, string SourcePath, string Source, string? ImmichAssetId, string? ImmichNewAssetId, int? Crf, string? OutputPath, decimal? PercentageSaved, string? FailureCategory, string? FailureMessage, DateTimeOffset UpdatedUtc);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(JobJsonOutput[]))]
internal sealed partial class QueueJsonContext : JsonSerializerContext;
