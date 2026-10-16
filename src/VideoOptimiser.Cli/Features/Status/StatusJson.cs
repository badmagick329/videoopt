using System.Text.Json.Serialization;

namespace VideoOptimiser.Cli.Features.Status;

/// <summary>The card as <c>status --json</c> prints it. Every field is always there, null when it has no value.</summary>
/// <param name="Run">Null when no run is going.</param>
/// <param name="Worker">Null when the lifecycle isn't Hetzner.</param>
/// <param name="Next">The commands only, as the text card prints them.</param>
internal sealed record StatusJsonOutput(StatusJsonQueue Queue, StatusJsonRun? Run, StatusJsonWorker? Worker, string[] Next);

internal sealed record StatusJsonQueue(int Converting, int Waiting, int Stopped, int Held, int ReadyToFinalise);

/// <param name="Background">A run with no terminal, started from the TUI; it stops with <c>run stop</c> rather than Ctrl+C.</param>
internal sealed record StatusJsonRun(string Command, DateTimeOffset StartedUtc, int ProcessId, bool Background, StatusJsonConverting[] Converting);

internal sealed record StatusJsonConverting(string Id, string File, string Status);

/// <param name="Exists">A server, a retained IPv4 address or a recorded worker: something that may bill.</param>
/// <param name="HetznerChecked">False when Hetzner wasn't asked or didn't answer; the server fields are then the recorded ones.</param>
/// <param name="UptimeSeconds">Null unless Hetzner answered with a running server's creation time.</param>
/// <param name="HourlyGrossEur">EUR per hour incl. VAT, from Hetzner.</param>
internal sealed record StatusJsonWorker(bool Exists, bool HetznerChecked, string? NotCheckedReason, string? Name, long? ServerId, string? Status, string? ServerType, string? Location, DateTimeOffset? CreatedUtc, long? UptimeSeconds, decimal? HourlyGrossEur);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(StatusJsonOutput))]
internal sealed partial class StatusJsonContext : JsonSerializerContext;
