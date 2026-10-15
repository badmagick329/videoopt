using System.Net;
using System.Text.Json;

namespace VideoOptimiser.Infrastructure.Hetzner;

internal readonly record struct ServerSnapshot(long Id, string Name, string Status, string Address, long PrimaryIpv4Id, IReadOnlyDictionary<string, string> Labels, DateTimeOffset? CreatedUtc, string ServerType, string Location);

internal readonly record struct LocationStatus(bool Available, bool Deprecated);

internal sealed record ServerTypeOffer(string Name, string Architecture, int Cores, decimal MemoryGiB, bool Deprecated, IReadOnlyDictionary<string, LocationStatus> Locations, IReadOnlyDictionary<string, decimal> HourlyGross);

internal sealed class HetznerApiException(HttpStatusCode statusCode, string? code, string message) : InvalidOperationException($"Hetzner API returned {(int)statusCode} ({statusCode}): {message}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
    public string ProviderMessage { get; } = message;
}

internal sealed class HetznerActionException(string? code, string message) : InvalidOperationException(message)
{
    public string? Code { get; } = code;
}

/// <summary>
/// A create response: the parsed server, and the action that provisions it. The action id is read on request because the
/// caller writes its state file between the two, as the create has always done.
/// </summary>
internal sealed class CreatedServer(JsonDocument document, ServerSnapshot server) : IDisposable
{
    public ServerSnapshot Server { get; } = server;

    public long? ReadActionId() =>
        document.RootElement.TryGetProperty("action", out var action) && action.TryGetProperty("id", out var actionId) ? actionId.GetInt64() : null;

    public void Dispose() => document.Dispose();
}
