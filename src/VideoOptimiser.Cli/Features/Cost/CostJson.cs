using System.Text.Json.Serialization;

namespace VideoOptimiser.Cli.Features.Cost;

/// <summary><c>cost --json</c>. Bytes are integers, EUR decimals rounded to 4 places, times ISO 8601 UTC.</summary>
internal sealed record CostJsonOutput(CostJsonLifetime Lifetime, CostJsonRun[] Runs);

/// <param name="EurPerTb">Null when nothing is freed yet.</param>
internal sealed record CostJsonLifetime(decimal BilledEur, long FreedBytes, long WaitingBytes, decimal? EurPerTb, int Workers, bool StillBilling, bool EndEstimated, bool PriceMissing);

/// <param name="EndUtc">The run's recorded end, or its last span's end when it never recorded one.</param>
/// <param name="WorkerSeconds">How long the run overlapped its worker's life.</param>
internal sealed record CostJsonRun(string Id, DateTimeOffset StartedUtc, DateTimeOffset EndUtc, long WorkerServerId, string WorkerName, decimal? HourlyGrossEur, long WorkerSeconds, decimal? Eur, long FreedBytes, long WaitingBytes, decimal? EurPerTb, int VideoCount, CostJsonVideo[] Videos);

/// <param name="State"><c>freed</c>, <c>waitingForReview</c> or <c>nothingSaved</c>.</param>
/// <param name="Status">The job status's enum name.</param>
internal sealed record CostJsonVideo(string JobId, string File, long WorkerSeconds, decimal? Eur, long SavedBytes, string State, string Status, decimal? EurPerTb, bool PriceMissing);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CostJsonOutput))]
internal sealed partial class CostJsonContext : JsonSerializerContext;
