using System.Net;
using System.Text.Json;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Infrastructure.Configuration;

namespace VideoOptimiser.Infrastructure.Immich;

public sealed class ImmichClientFactory : IImmichClientFactory, IDisposable
{
    private readonly HttpClient _http = new();

    public IImmichClient Create(ImmichSettings settings) => new ImmichHttpClient(_http, settings);

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Calls the Immich REST API (checked against the v3.2.2 OpenAPI spec). The key is loaded on the first request that needs
/// it, so the unauthenticated version check still works, and reports a missing key, without one.
/// </summary>
public sealed class ImmichHttpClient(HttpClient http, ImmichSettings settings) : IImmichClient
{
    private readonly Uri _apiBase = ApiBase(settings.BaseUrl);
    private string? _apiKey;

    public async Task<ImmichServerVersion> GetServerVersionAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync("server/version", permission: null, cancellationToken);
        var root = document.RootElement;
        return new ImmichServerVersion(root.GetProperty("major").GetInt32(), root.GetProperty("minor").GetInt32(), root.GetProperty("patch").GetInt32());
    }

    public async Task<ImmichUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync("users/me", permission: "user.read", cancellationToken);
        var root = document.RootElement;
        return new ImmichUser(root.GetProperty("id").GetString()!, root.GetProperty("name").GetString()!);
    }

    /// <summary>
    /// The API lives under <c>/api</c>. Users copy the web address, so a URL with or without that suffix is accepted.
    /// </summary>
    internal static Uri ApiBase(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^4];
        return new Uri(trimmed + "/api/");
    }

    // permission is null for endpoints that need no key; otherwise it names the scope a 403 means is missing.
    private async Task<JsonDocument> GetJsonAsync(string path, string? permission, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBase, path));
        request.Headers.Accept.ParseAdd("application/json");
        if (permission is not null) request.Headers.Add("x-api-key", ApiKey());
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ImmichApiException("ImmichUnavailable", $"Immich is unreachable at {settings.BaseUrl}: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ImmichApiException("ImmichUnavailable", $"Immich at {settings.BaseUrl} did not respond in time.");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new ImmichApiException("ImmichApiKeyRejected", $"Immich rejected the API key in {settings.ApiKeyEnvironmentVariable} (HTTP 401). Create a new key in Immich under Account settings > API keys.");
                case HttpStatusCode.Forbidden:
                    throw new ImmichApiException("ImmichPermissionMissing", $"The Immich API key lacks the {permission} permission (HTTP 403). Add it to the key in Immich under Account settings > API keys.");
                case var status when !response.IsSuccessStatusCode:
                    throw new ImmichApiException("ImmichRequestFailed", $"Immich returned HTTP {(int)status} for GET /api/{path}.");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        }
    }

    private string ApiKey() => _apiKey ??= SecretLoader.Load(settings.ApiKeyEnvironmentVariable, settings.ApiKeyFile)
        ?? throw new ImmichApiException("ImmichApiKeyMissing", $"Immich API key '{settings.ApiKeyEnvironmentVariable}' was not found in the environment or '{settings.ApiKeyFile}'. Add it to the ignored key file or set the environment variable.");
}
