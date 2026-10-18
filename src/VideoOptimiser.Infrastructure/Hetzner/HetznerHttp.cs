using System.Net.Http.Headers;
using System.Text.Json;

namespace VideoOptimiser.Infrastructure.Hetzner;

/// <summary>
/// The request and error plumbing every Hetzner client repeated. Each client still owns its user agent, its exception
/// type and its message, so a Hetzner access log and the user-facing wording stay per feature.
/// </summary>
internal static class HetznerHttp
{
    public const string ApiBase = "https://api.hetzner.cloud/v1/";

    /// <summary>
    /// Sends one request with the bearer token and, when there is a body, a JSON content type. The response is returned
    /// once its headers arrive; the caller reads and disposes it.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpClient.Timeout"/> firing throws <see cref="TaskCanceledException"/>, which every caller up to the TUI and
    /// <c>Program</c> would read as the user stopping. It becomes a <see cref="TimeoutException"/> here, so a Hetzner that
    /// doesn't answer is a failure; a cancelled caller still gets its cancellation.
    /// </remarks>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, string token, string userAgent, HttpMethod method, string path, byte[]? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd(userAgent);
        if (payload is not null) request.Content = new ByteArrayContent(payload) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Hetzner didn't answer {method} {RequestName(path)} within {http.Timeout.TotalSeconds:0.###} seconds.", exception);
        }
    }

    /// <summary>The path without its query, which holds paging and label selectors nobody reading the error needs.</summary>
    private static string RequestName(string path) => path.Split('?', 2)[0];

    /// <summary>
    /// Reads Hetzner's <c>error.code</c> and <c>error.message</c>. A field counts only when it is a JSON string; anything
    /// else, or a body that is not JSON, leaves the code null and the message as the raw body, so a malformed error body
    /// still produces a readable failure instead of a JSON-type exception.
    /// </summary>
    public static async Task<(string? Code, string Message)> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = body;
        string? code = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String) code = codeElement.GetString();
                if (error.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String) message = value.GetString()!;
            }
        }
        catch (JsonException) { }
        return (code, message);
    }
}
