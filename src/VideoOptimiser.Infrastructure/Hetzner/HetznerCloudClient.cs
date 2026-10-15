using System.Globalization;
using System.Net;
using System.Text.Json;
using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Infrastructure.Hetzner;

/// <summary>
/// The Hetzner Cloud calls the managed worker needs: servers, actions, Primary IPs and server types, with their paths,
/// attempt counts and delays. The ownership labels live here too, because every call that creates, finds or deletes a
/// server must agree on them; where a server should go and when it is created stay with the worker slice.
/// </summary>
internal sealed class HetznerCloudClient : IDisposable
{
    internal const string ManagedLabel = "video-optimiser-managed";
    internal const string ControllerLabel = "video-optimiser-controller";
    private const string UserAgent = "video-optimiser/managed-worker";
    private const int MaxServerTypePages = 10;
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly bool _ownsHttpClient;

    public HetznerCloudClient(HttpClient http, bool ownsHttpClient, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(HetznerHttp.ApiBase);
        _delay = delay;
        _ownsHttpClient = ownsHttpClient;
    }

    /// <summary>
    /// Posts the create request and parses the server it answers with. A failed POST throws before anything exists, and the
    /// caller decides whether that failure means "try the next placement".
    /// </summary>
    public async Task<CreatedServer> CreateServerAsync(string token, string name, HetznerSettings hetzner, string serverType, string location, string controller, string bootstrapScript, CancellationToken cancellationToken)
    {
        var document = await SendJsonAsync(token, HttpMethod.Post, "servers", CreateServerPayload(name, hetzner, serverType, location, controller, bootstrapScript), cancellationToken);
        try
        {
            return new CreatedServer(document, ParseServer(document.RootElement.GetProperty("server")));
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public async Task<List<ServerSnapshot>> ListManagedServersAsync(string token, string controller, CancellationToken cancellationToken)
    {
        var selector = Uri.EscapeDataString($"{ManagedLabel}=true,{ControllerLabel}={controller}");
        using var document = await SendJsonAsync(token, HttpMethod.Get, $"servers?label_selector={selector}", null, cancellationToken);
        return document.RootElement.GetProperty("servers").EnumerateArray().Select(ParseServer).ToList();
    }

    public async Task<ServerSnapshot?> GetServerAsync(string token, long id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(token, HttpMethod.Get, $"servers/{id.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var document = await ReadSuccessJsonAsync(response, cancellationToken);
        return ParseServer(document.RootElement.GetProperty("server"));
    }

    public async Task<long?> DeleteServerAsync(string token, long id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(token, HttpMethod.Delete, $"servers/{id.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var document = await ReadSuccessJsonAsync(response, cancellationToken);
        return document.RootElement.TryGetProperty("action", out var action) ? action.GetProperty("id").GetInt64() : null;
    }

    public async Task WaitForActionAsync(string token, long actionId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            using var document = await SendJsonAsync(token, HttpMethod.Get, $"actions/{actionId.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
            var action = document.RootElement.GetProperty("action");
            var status = action.GetProperty("status").GetString();
            if (status == "success") return;
            if (status == "error")
            {
                var error = action.TryGetProperty("error", out var errorElement) ? errorElement : default;
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString() ?? "unknown action error"
                    : "unknown action error";
                var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeElement)
                    ? codeElement.GetString()
                    : null;
                throw new HetznerActionException(code, message);
            }
            await _delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException($"Hetzner action {actionId} did not complete in time.");
    }

    public async Task WaitForServerDeletionAsync(string token, long serverId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await GetServerAsync(token, serverId, cancellationToken) is null) return;
            await _delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException($"Hetzner worker {serverId} still exists after its delete action completed.");
    }

    public async Task SetPrimaryIpAutoDeleteAsync(string token, long id, string controller, CancellationToken cancellationToken)
    {
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("auto_delete", true);
            writer.WritePropertyName("labels");
            writer.WriteStartObject();
            writer.WriteString(ManagedLabel, "true");
            writer.WriteString(ControllerLabel, controller);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using var document = await SendJsonAsync(token, HttpMethod.Put, $"primary_ips/{id.ToString(CultureInfo.InvariantCulture)}", payload.ToArray(), cancellationToken);
    }

    public async Task<bool> DeleteOrphanedPrimaryIpsAsync(string token, string controller, CancellationToken cancellationToken)
    {
        var selector = Uri.EscapeDataString($"{ManagedLabel}=true,{ControllerLabel}={controller}");
        using var document = await SendJsonAsync(token, HttpMethod.Get, $"primary_ips?label_selector={selector}", null, cancellationToken);
        var ids = document.RootElement.GetProperty("primary_ips").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).ToArray();
        if (ids.Length > 1) throw new InvalidOperationException("More than one managed Primary IPv4 has this controller label; refusing automatic deletion.");
        if (ids.Length == 0) return false;
        await DeletePrimaryIpAsync(token, ids[0], cancellationToken);
        return true;
    }

    public async Task<bool> PrimaryIpExistsAsync(string token, long id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(token, HttpMethod.Get, $"primary_ips/{id.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        using var document = await ReadSuccessJsonAsync(response, cancellationToken);
        return document.RootElement.TryGetProperty("primary_ip", out _);
    }

    public async Task DeletePrimaryIpAsync(string token, long id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(token, HttpMethod.Delete, $"primary_ips/{id.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode) throw await ApiExceptionAsync(response, cancellationToken);
    }

    public async Task<Dictionary<string, ServerTypeOffer>> FetchServerTypesAsync(string token, IReadOnlyCollection<string> wanted, CancellationToken cancellationToken)
    {
        // One listing covers every configured type; Hetzner's name filter takes a single name.
        var offers = new Dictionary<string, ServerTypeOffer>(StringComparer.OrdinalIgnoreCase);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? page = 1;
        for (var requests = 0; page is not null; requests++)
        {
            if (requests == MaxServerTypePages) throw PreflightFailure($"the server type listing did not end within {MaxServerTypePages} pages");
            JsonDocument document;
            try
            {
                document = await SendJsonAsync(token, HttpMethod.Get, $"server_types?per_page=50&page={page}", null, cancellationToken);
            }
            catch (JsonException exception)
            {
                throw PreflightFailure($"the response was not valid JSON: {exception.Message}", exception);
            }
            using (document)
            {
                page = ParseServerTypesPage(document.RootElement, wanted, listed, offers);
            }
        }
        return offers;
    }

    private async Task<JsonDocument> SendJsonAsync(string token, HttpMethod method, string path, byte[]? payload, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(token, method, path, payload, cancellationToken);
        return await ReadSuccessJsonAsync(response, cancellationToken);
    }

    private Task<HttpResponseMessage> SendAsync(string token, HttpMethod method, string path, byte[]? payload, CancellationToken cancellationToken) =>
        HetznerHttp.SendAsync(_http, token, UserAgent, method, path, payload, cancellationToken);

    private static async Task<JsonDocument> ReadSuccessJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) throw await ApiExceptionAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task<HetznerApiException> ApiExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var (code, message) = await HetznerHttp.ReadErrorAsync(response, cancellationToken);
        return new HetznerApiException(response.StatusCode, code, message);
    }

    private static int? ParseServerTypesPage(JsonElement root, IReadOnlyCollection<string> wanted, HashSet<string> listed, Dictionary<string, ServerTypeOffer> offers)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("server_types", out var serverTypes) || serverTypes.ValueKind != JsonValueKind.Array)
            throw PreflightFailure("the response did not contain a server_types array");
        foreach (var serverType in serverTypes.EnumerateArray())
        {
            if (serverType.ValueKind != JsonValueKind.Object || !serverType.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                throw PreflightFailure("the server_types array contained an entry without a string name");
            var name = nameElement.GetString()!;
            if (!listed.Add(name)) throw PreflightFailure($"server type '{name}' was listed more than once");
            if (wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) offers.Add(name, ParseOffer(serverType, name));
        }

        return root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
               meta.TryGetProperty("pagination", out var pagination) && pagination.ValueKind == JsonValueKind.Object &&
               pagination.TryGetProperty("next_page", out var next) && next.ValueKind == JsonValueKind.Number
            ? next.GetInt32()
            : null;
    }

    private static ServerTypeOffer ParseOffer(JsonElement serverType, string name)
    {
        if (!serverType.TryGetProperty("architecture", out var architecture) || architecture.ValueKind != JsonValueKind.String)
            throw PreflightFailure($"server type '{name}' did not contain a string architecture");
        if (!serverType.TryGetProperty("cores", out var cores) || !cores.TryGetInt32(out var coreCount))
            throw PreflightFailure($"server type '{name}' did not contain an integer cores field");
        if (!serverType.TryGetProperty("memory", out var memory) || !memory.TryGetDecimal(out var memoryGiB))
            throw PreflightFailure($"server type '{name}' did not contain a numeric memory field");
        var deprecated = serverType.TryGetProperty("deprecated", out var deprecatedField) && deprecatedField.ValueKind == JsonValueKind.True ||
                         serverType.TryGetProperty("deprecation", out var deprecation) && deprecation.ValueKind == JsonValueKind.Object;

        if (!serverType.TryGetProperty("locations", out var locationArray) || locationArray.ValueKind != JsonValueKind.Array)
            throw PreflightFailure($"server type '{name}' did not contain a locations array");
        var locations = new Dictionary<string, LocationStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in locationArray.EnumerateArray())
        {
            if (location.ValueKind != JsonValueKind.Object ||
                !location.TryGetProperty("name", out var locationName) || locationName.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(locationName.GetString()))
                throw PreflightFailure($"the locations array of '{name}' contained an entry without a non-empty string name");
            if (!location.TryGetProperty("available", out var available) || available.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw PreflightFailure($"location '{locationName.GetString()}' of '{name}' did not contain a boolean available field");
            var locationDeprecated = location.TryGetProperty("deprecation", out var locationDeprecation) && locationDeprecation.ValueKind == JsonValueKind.Object;
            if (!locations.TryAdd(locationName.GetString()!, new LocationStatus(available.GetBoolean(), locationDeprecated)))
                throw PreflightFailure($"the locations array of '{name}' contained duplicate location '{locationName.GetString()}'");
        }

        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (serverType.TryGetProperty("prices", out var priceArray) && priceArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var price in priceArray.EnumerateArray())
            {
                if (price.ValueKind != JsonValueKind.Object ||
                    !price.TryGetProperty("location", out var location) || location.ValueKind != JsonValueKind.String ||
                    !price.TryGetProperty("price_hourly", out var hourly) || hourly.ValueKind != JsonValueKind.Object ||
                    !hourly.TryGetProperty("gross", out var gross) || gross.ValueKind != JsonValueKind.String ||
                    !decimal.TryParse(gross.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var grossValue))
                    throw PreflightFailure($"the prices array of '{name}' contained an entry without a location and a decimal price_hourly.gross");
                prices[location.GetString()!] = grossValue;
            }
        }

        return new ServerTypeOffer(name, architecture.GetString()!, coreCount, memoryGiB, deprecated, locations, prices);
    }

    private static InvalidOperationException PreflightFailure(string reason, Exception? innerException = null) =>
        new($"Hetzner server type preflight failed: {reason}; no worker was created.", innerException);

    private static byte[] CreateServerPayload(string name, HetznerSettings hetzner, string serverType, string location, string controller, string bootstrapScript)
    {
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("server_type", serverType);
            writer.WriteString("image", hetzner.Image);
            writer.WriteString("location", location);
            writer.WritePropertyName("ssh_keys");
            writer.WriteStartArray();
            writer.WriteStringValue(hetzner.SshKeyName);
            writer.WriteEndArray();
            writer.WriteString("user_data", bootstrapScript);
            writer.WritePropertyName("labels");
            writer.WriteStartObject();
            writer.WriteString(ManagedLabel, "true");
            writer.WriteString(ControllerLabel, controller);
            writer.WriteEndObject();
            writer.WritePropertyName("public_net");
            writer.WriteStartObject();
            writer.WriteBoolean("enable_ipv4", true);
            writer.WriteBoolean("enable_ipv6", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return payload.ToArray();
    }

    private static ServerSnapshot ParseServer(JsonElement server)
    {
        var publicNet = server.GetProperty("public_net");
        var ipv4 = publicNet.GetProperty("ipv4");
        var labels = server.GetProperty("labels").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
        DateTimeOffset? created = server.TryGetProperty("created", out var createdElement) && DateTimeOffset.TryParse(createdElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
        // Informational only: shown by worker status, never used to decide anything.
        var serverType = server.TryGetProperty("server_type", out var typeElement) && typeElement.ValueKind == JsonValueKind.Object &&
                         typeElement.TryGetProperty("name", out var typeName) && typeName.ValueKind == JsonValueKind.String
            ? typeName.GetString()!
            : string.Empty;
        var location = server.TryGetProperty("datacenter", out var datacenter) && datacenter.ValueKind == JsonValueKind.Object &&
                       datacenter.TryGetProperty("location", out var locationElement) && locationElement.ValueKind == JsonValueKind.Object &&
                       locationElement.TryGetProperty("name", out var locationName) && locationName.ValueKind == JsonValueKind.String
            ? locationName.GetString()!
            : string.Empty;
        return new ServerSnapshot(
            server.GetProperty("id").GetInt64(),
            server.GetProperty("name").GetString() ?? string.Empty,
            server.GetProperty("status").GetString() ?? "unknown",
            ipv4.GetProperty("ip").GetString() ?? throw new InvalidOperationException("The Hetzner worker has no public IPv4 address."),
            ipv4.TryGetProperty("id", out var primaryId) && primaryId.ValueKind == JsonValueKind.Number ? primaryId.GetInt64() : 0,
            labels,
            created,
            serverType,
            location,
            ParseHourlyGross(server, location));
    }

    /// <summary>
    /// The server type's hourly gross price in the server's own location, from the <c>prices</c> array a server carries.
    /// Informational, so a missing or odd array gives null instead of failing the read.
    /// </summary>
    private static decimal? ParseHourlyGross(JsonElement server, string location)
    {
        if (!server.TryGetProperty("server_type", out var type) || type.ValueKind != JsonValueKind.Object ||
            !type.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var price in prices.EnumerateArray())
        {
            if (price.ValueKind == JsonValueKind.Object &&
                price.TryGetProperty("location", out var priceLocation) && priceLocation.ValueKind == JsonValueKind.String &&
                string.Equals(priceLocation.GetString(), location, StringComparison.OrdinalIgnoreCase) &&
                price.TryGetProperty("price_hourly", out var hourly) && hourly.ValueKind == JsonValueKind.Object &&
                hourly.TryGetProperty("gross", out var gross) && gross.ValueKind == JsonValueKind.String &&
                decimal.TryParse(gross.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                return value;
        }
        return null;
    }

    public static void VerifyOwnership(ServerSnapshot server, string controller)
    {
        if (server.Labels.GetValueOrDefault(ManagedLabel) != "true" || server.Labels.GetValueOrDefault(ControllerLabel) != controller)
            throw new InvalidOperationException($"The Hetzner worker {server.Name} does not have the expected ownership labels; refusing to reuse or delete it.");
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
