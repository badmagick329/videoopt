using System.Net;
using System.Text.Json;

namespace VideoOptimiser.Infrastructure.Hetzner;

internal sealed record HetznerSshKey(long Id, string Name, string Fingerprint);

/// <param name="statusCode">Null when Hetzner answered with success but the body was not what the API documents.</param>
internal sealed class HetznerSshKeyException(HttpStatusCode? statusCode, string message) : InvalidOperationException(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>
/// The two Hetzner calls remote setup needs. Kept apart from the worker lifecycle because setup runs before any
/// lifecycle exists and must not depend on a configured database path.
/// </summary>
internal sealed class HetznerSshKeyClient : IDisposable
{
    private const string UserAgent = "video-optimiser/remote-setup";
    private const int MaxPages = 10;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public HetznerSshKeyClient()
        : this(new HttpClient { BaseAddress = new Uri(HetznerHttp.ApiBase) }, ownsHttpClient: true)
    {
    }

    public HetznerSshKeyClient(HttpClient http, bool ownsHttpClient = false)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(HetznerHttp.ApiBase);
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<IReadOnlyList<HetznerSshKey>> ListAsync(string token, CancellationToken cancellationToken)
    {
        var keys = new List<HetznerSshKey>();
        int? page = 1;
        for (var requests = 0; page is not null; requests++)
        {
            if (requests == MaxPages) throw new HetznerSshKeyException(null, $"the SSH key listing did not end within {MaxPages} pages");
            using var document = await SendJsonAsync(token, HttpMethod.Get, $"ssh_keys?per_page=50&page={page}", null, cancellationToken);
            page = ParsePage(document.RootElement, keys);
        }
        return keys;
    }

    public async Task<HetznerSshKey> CreateAsync(string token, string name, string publicKey, CancellationToken cancellationToken)
    {
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("public_key", publicKey);
            writer.WriteEndObject();
        }
        using var document = await SendJsonAsync(token, HttpMethod.Post, "ssh_keys", payload.ToArray(), cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("ssh_key", out var key))
            throw Malformed("the response did not contain an ssh_key");
        return ParseKey(key);
    }

    private static int? ParsePage(JsonElement root, List<HetznerSshKey> keys)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ssh_keys", out var array) || array.ValueKind != JsonValueKind.Array)
            throw Malformed("the response did not contain an ssh_keys array");
        keys.AddRange(array.EnumerateArray().Select(ParseKey));
        return root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
               meta.TryGetProperty("pagination", out var pagination) && pagination.ValueKind == JsonValueKind.Object &&
               pagination.TryGetProperty("next_page", out var next) && next.ValueKind == JsonValueKind.Number
            ? next.GetInt32()
            : null;
    }

    private static HetznerSshKey ParseKey(JsonElement key)
    {
        if (key.ValueKind != JsonValueKind.Object ||
            !key.TryGetProperty("id", out var id) || !id.TryGetInt64(out var idValue) ||
            !key.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            !key.TryGetProperty("fingerprint", out var fingerprint) || fingerprint.ValueKind != JsonValueKind.String)
            throw Malformed("an SSH key had no id, name and fingerprint");
        return new HetznerSshKey(idValue, name.GetString()!, fingerprint.GetString()!);
    }

    private static HetznerSshKeyException Malformed(string reason) => new(null, $"Hetzner returned an unexpected SSH key response: {reason}.");

    private async Task<JsonDocument> SendJsonAsync(string token, HttpMethod method, string path, byte[]? payload, CancellationToken cancellationToken)
    {
        using var response = await HetznerHttp.SendAsync(_http, token, UserAgent, method, path, payload, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await ApiExceptionAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw Malformed(exception.Message);
        }
    }

    private static async Task<HetznerSshKeyException> ApiExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var (_, message) = await HetznerHttp.ReadErrorAsync(response, cancellationToken);
        return new HetznerSshKeyException(response.StatusCode, $"Hetzner API returned {(int)response.StatusCode} ({response.StatusCode}): {message}");
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
