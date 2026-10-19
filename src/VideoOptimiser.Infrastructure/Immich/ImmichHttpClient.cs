using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Infrastructure.Configuration;

namespace VideoOptimiser.Infrastructure.Immich;

public sealed class ImmichClientFactory : IImmichClientFactory, IDisposable
{
    // Transfers of multi-GB originals outlast HttpClient's 100 s default, so the shared client has no overall timeout;
    // ImmichHttpClient times each request itself.
    private readonly HttpClient _http = new(CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Redirects are never followed: .NET strips only <c>Authorization</c> on a redirect, so <c>x-api-key</c>, which gives full
    /// access to the library, would reach whatever host the redirect names. ImmichHttpClient reports a 3xx answer instead.
    /// </summary>
    internal static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false };

    public IImmichClient Create(ImmichSettings settings) => new ImmichHttpClient(_http, settings);

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Calls the Immich REST API (checked against the v3.2.2 OpenAPI spec). The key is loaded on the first request that needs
/// it, so the unauthenticated version check still works, and reports a missing key, without one.
/// </summary>
public sealed class ImmichHttpClient : IImmichClient
{
    private const int SearchPageSize = 250;
    private const int CopyBufferSize = 1 << 20;
    private static readonly ImmichJsonContext Json = ImmichJsonContext.Default;

    private readonly HttpClient _http;
    private readonly ImmichSettings _settings;
    private readonly Uri _apiBase;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _idleTimeout;
    private string? _apiKey;
    private ImmichTrashSettings? _trash;

    public ImmichHttpClient(HttpClient http, ImmichSettings settings)
        : this(http, settings, requestTimeout: TimeSpan.FromSeconds(100), idleTimeout: TimeSpan.FromMinutes(5))
    {
    }

    /// <param name="requestTimeout">Limit for a whole JSON request.</param>
    /// <param name="idleTimeout">
    /// Limit on a transfer going without a byte read. A multi-GB transfer is slow but steady, so only a stall fails it; the
    /// wait for Immich's answer after the last upload byte counts too, which is why it is generous.
    /// </param>
    internal ImmichHttpClient(HttpClient http, ImmichSettings settings, TimeSpan requestTimeout, TimeSpan idleTimeout)
    {
        _http = http;
        _settings = settings;
        _apiBase = ApiBase(settings.BaseUrl);
        _requestTimeout = requestTimeout;
        _idleTimeout = idleTimeout;
    }

    public async Task<ImmichServerVersion> GetServerVersionAsync(CancellationToken cancellationToken = default)
    {
        var version = await SendJsonAsync(HttpMethod.Get, "server/version", null, permission: null, Json.ServerVersionDto, cancellationToken);
        return new ImmichServerVersion(version.Major, version.Minor, version.Patch);
    }

    public async Task<ImmichUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var user = await SendJsonAsync(HttpMethod.Get, "users/me", null, "user.read", Json.UserDto, cancellationToken);
        return new ImmichUser(user.Id, user.Name);
    }

    public async Task<ImmichTrashSettings> GetTrashSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_trash is not null) return _trash;
        // Both endpoints are public: the web client reads them before login.
        var features = await SendJsonAsync(HttpMethod.Get, "server/features", null, permission: null, Json.ServerFeaturesDto, cancellationToken);
        var config = await SendJsonAsync(HttpMethod.Get, "server/config", null, permission: null, Json.ServerConfigDto, cancellationToken);
        return _trash = new ImmichTrashSettings(features.Trash, config.TrashDays);
    }

    public async IAsyncEnumerable<ImmichAsset> SearchVideosAsync(DateTimeOffset takenBefore, bool includeFavorites, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filter = new SearchFilterDto
        {
            Type = new ValueFilter { Eq = ImmichValues.Video },
            TakenAt = new DateFilter { Lt = takenBefore.ToUniversalTime() },
            // Leaves out hidden motion-photo parts and the locked folder.
            Visibility = new ListFilter { In = [ImmichValues.Timeline, ImmichValues.Archive] },
            // External-library files never came from the phone.
            LibraryId = new NullFilter(),
            TrashedAt = new NullFilter(),
            IsFavorite = includeFavorites ? null : new BoolFilter { Eq = false }
        };
        string? cursor = null;
        do
        {
            var request = new MetadataSearchRequest
            {
                Filter = filter,
                Size = SearchPageSize,
                Cursor = cursor,
                WithExif = true,
                OrderBy = new SearchOrderDto { Field = "fileCreatedAt", Direction = "asc" }
            };
            var page = await SendJsonAsync(HttpMethod.Post, "search/metadata", JsonContent.Create(request, Json.MetadataSearchRequest), "asset.read", Json.SearchResponseDto, cancellationToken);
            foreach (var asset in page.Assets.Items)
            {
                yield return Map(asset);
            }

            if (page.Assets.NextCursor is not null && page.Assets.NextCursor == cursor)
            {
                throw new ImmichApiException("ImmichRequestFailed", "Immich returned the same search cursor twice; stopping rather than looping.");
            }

            cursor = page.Assets.NextCursor;
        }
        while (cursor is not null);
    }

    public async Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default) =>
        Map(await SendJsonAsync(HttpMethod.Get, AssetPath(assetId), null, "asset.read", Json.AssetDto, cancellationToken));

    public async Task DownloadOriginalAsync(string assetId, string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var path = AssetPath(assetId) + "/original";
        var partPath = destinationPath + ".part";
        try
        {
            await WithTimeoutAsync(HttpMethod.Get, path, _idleTimeout, async (token, timer) =>
            {
                using var response = await SendRawAsync(HttpMethod.Get, path, null, "asset.download", token);
                var expected = response.Content.Headers.ContentLength;
                await using var source = new TransferStream(await response.Content.ReadAsStreamAsync(token), timer, _idleTimeout, progress);
                await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
                {
                    await source.CopyToAsync(target, CopyBufferSize, token);
                }

                if (expected is { } length && source.Transferred != length)
                {
                    throw new ImmichApiException("ImmichUnavailable", $"The download of asset {assetId} ended after {source.Transferred} of {length} bytes.");
                }

                return true;
            }, cancellationToken);
            File.Move(partPath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    public async Task<ImmichUploadResult> UploadAsync(ImmichUpload upload, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var response = await WithTimeoutAsync(HttpMethod.Post, "assets", _idleTimeout, async (token, timer) =>
        {
            await using var file = new FileStream(upload.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
            // StreamContent reads the file as the request is sent, so the body is never held in memory.
            var fileContent = new StreamContent(new TransferStream(file, timer, _idleTimeout, progress), CopyBufferSize);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var content = new MultipartFormDataContent
            {
                { new StringContent(FormatDate(upload.FileCreatedAt)), "fileCreatedAt" },
                { new StringContent(FormatDate(upload.FileModifiedAt)), "fileModifiedAt" },
                { new StringContent(upload.IsFavorite ? "true" : "false"), "isFavorite" },
                { new StringContent(upload.FileName), "filename" },
                { fileContent, "assetData", upload.FileName }
            };
            using var message = await SendRawAsync(HttpMethod.Post, "assets", content, "asset.upload", token);
            await using var body = await message.Content.ReadAsStreamAsync(token);
            return await JsonSerializer.DeserializeAsync(body, Json.UploadResponseDto, token);
        }, cancellationToken);

        if (response is null || string.IsNullOrEmpty(response.Id))
        {
            throw new ImmichApiException("ImmichRequestFailed", "Immich accepted the upload but returned no asset ID.");
        }

        return new ImmichUploadResult(response.Id, response.Status == "duplicate");
    }

    public Task UpdateMetadataAsync(string assetId, ImmichMetadataUpdate update, CancellationToken cancellationToken = default)
    {
        var request = new UpdateAssetRequest
        {
            Description = update.Description,
            Rating = update.Rating
        };
        return SendNoContentAsync(HttpMethod.Put, AssetPath(assetId), JsonContent.Create(request, Json.UpdateAssetRequest), "asset.update", cancellationToken);
    }

    public Task CopyRelationsAsync(string sourceAssetId, string targetAssetId, CancellationToken cancellationToken = default)
    {
        var request = new AssetCopyRequest
        {
            SourceId = sourceAssetId,
            TargetId = targetAssetId,
            Albums = true,
            Favorite = true,
            SharedLinks = true,
            Stack = true,
            Sidecar = true
        };
        return SendNoContentAsync(HttpMethod.Put, "assets/copy", JsonContent.Create(request, Json.AssetCopyRequest), "asset.copy", cancellationToken);
    }

    public Task TagAssetAsync(string assetId, IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default)
    {
        if (tagIds.Count == 0) return Task.CompletedTask;
        var request = new TagAssetsRequest { AssetIds = [assetId], TagIds = tagIds };
        return SendNoContentAsync(HttpMethod.Put, "tags/assets", JsonContent.Create(request, Json.TagAssetsRequest), "tag.asset", cancellationToken);
    }

    public async Task TrashAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default)
    {
        // Checked here as well as at the start of each run, so no caller can trash while trashing would purge.
        await ImmichTrash.RequireOnAsync(this, cancellationToken);
        var request = new AssetDeleteRequest { Ids = assetIds, Force = false };
        await SendNoContentAsync(HttpMethod.Delete, "assets", JsonContent.Create(request, Json.AssetDeleteRequest), "asset.delete", cancellationToken);
    }

    public async Task<IReadOnlyList<ImmichUploadCheck>> CheckUploadsAsync(IReadOnlyList<string> checksums, CancellationToken cancellationToken = default)
    {
        if (checksums.Count == 0) return [];
        // Each item's ID is its index, so results map back to checksums even if Immich reorders them.
        var request = new UploadCheckRequest
        {
            Assets = checksums.Select((checksum, index) => new UploadCheckItem { Id = index.ToString(CultureInfo.InvariantCulture), Checksum = checksum }).ToList()
        };
        var response = await SendJsonAsync(HttpMethod.Post, "assets/bulk-upload-check", JsonContent.Create(request, Json.UploadCheckRequest), "asset.upload", Json.UploadCheckResponse, cancellationToken);
        var byIndex = response.Results.ToDictionary(result => result.Id);
        return checksums.Select((checksum, index) =>
        {
            if (!byIndex.TryGetValue(index.ToString(CultureInfo.InvariantCulture), out var result))
            {
                throw new ImmichApiException("ImmichRequestFailed", $"Immich's upload check returned no result for checksum {checksum}.");
            }

            return new ImmichUploadCheck(checksum, result.Action == "reject", result.Reason, result.AssetId, result.IsTrashed);
        }).ToList();
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

    private static string AssetPath(string assetId) => "assets/" + Uri.EscapeDataString(assetId);

    private static string FormatDate(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static ImmichAsset Map(AssetDto asset)
    {
        var exif = asset.ExifInfo;
        return new ImmichAsset(
            asset.Id,
            asset.Checksum,
            asset.OriginalFileName,
            asset.Type,
            asset.Visibility,
            asset.IsFavorite,
            asset.IsTrashed,
            asset.LibraryId,
            asset.LivePhotoVideoId,
            asset.FileCreatedAt,
            asset.FileModifiedAt,
            asset.LocalDateTime,
            asset.Duration,
            new ImmichExif(exif?.FileSizeInByte, exif?.DateTimeOriginal, exif?.Latitude, exif?.Longitude, exif?.Description, exif?.Rating, exif?.Make, exif?.Model),
            asset.Tags?.Select(tag => tag.Id).ToList() ?? [],
            asset.Width,
            asset.Height);
    }

    private Task<T> SendJsonAsync<T>(HttpMethod method, string path, HttpContent? content, string? permission, JsonTypeInfo<T> responseType, CancellationToken cancellationToken) =>
        WithTimeoutAsync(method, path, _requestTimeout, async (token, _) =>
        {
            using var response = await SendRawAsync(method, path, content, permission, token);
            await using var body = await response.Content.ReadAsStreamAsync(token);
            return await JsonSerializer.DeserializeAsync(body, responseType, token)
                ?? throw new ImmichApiException("ImmichRequestFailed", $"Immich returned an empty body for {method} /api/{path}.");
        }, cancellationToken);

    private async Task SendNoContentAsync(HttpMethod method, string path, HttpContent content, string permission, CancellationToken cancellationToken) =>
        await WithTimeoutAsync(method, path, _requestTimeout, async (token, _) =>
        {
            using var response = await SendRawAsync(method, path, content, permission, token);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Runs one request under a timer that <paramref name="timeout"/> arms; transfers re-arm it on every read. Turns
    /// network failures and the timer firing into <c>ImmichUnavailable</c>, and leaves the caller's own cancellation alone.
    /// </summary>
    private async Task<T> WithTimeoutAsync<T>(HttpMethod method, string path, TimeSpan timeout, Func<CancellationToken, CancellationTokenSource, Task<T>> action, CancellationToken cancellationToken)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            return await action(timer.Token, timer);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ImmichApiException("ImmichUnavailable", $"Immich at {_settings.BaseUrl} did not respond for {timeout.TotalSeconds:0} s during {method} /api/{path}.");
        }
        catch (HttpRequestException exception)
        {
            throw new ImmichApiException("ImmichUnavailable", $"Immich is unreachable at {_settings.BaseUrl}: {exception.Message}");
        }
        catch (IOException exception) when (exception is HttpIOException || exception.InnerException is SocketException)
        {
            throw new ImmichApiException("ImmichUnavailable", $"The connection to Immich at {_settings.BaseUrl} broke during {method} /api/{path}: {exception.Message}");
        }
    }

    // permission is null for endpoints that need no key; otherwise it names the scope a 403 means is missing.
    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, HttpContent? content, string? permission, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_apiBase, path)) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        if (permission is not null) request.Headers.Add("x-api-key", ApiKey());
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new ImmichApiException("ImmichApiKeyRejected", $"Immich rejected the API key in {_settings.ApiKeyEnvironmentVariable} (HTTP 401). Create a new key in Immich under Account settings > API keys."),
                HttpStatusCode.Forbidden => new ImmichApiException("ImmichPermissionMissing", $"The Immich API key lacks the {permission} permission (HTTP 403). Add it to the key in Immich under Account settings > API keys."),
                var status when (int)status is >= 300 and < 400 && response.Headers.Location is { } location => Redirected(method, path, status, new Uri(request.RequestUri!, location)),
                var status => new ImmichApiException("ImmichRequestFailed", $"Immich returned HTTP {(int)status} for {method} /api/{path}{await ErrorDetailAsync(response, cancellationToken)}", (int)status)
            };
        }
    }

    /// <summary>
    /// A redirect fails every request alike, so it gets its own code rather than <c>ImmichRequestFailed</c>, which callers read
    /// as one asset's failure. It almost always means <c>baseUrl</c> names the http side of an https proxy; when the target
    /// is the same API path under another base, that base is the one to configure.
    /// </summary>
    private ImmichApiException Redirected(HttpMethod method, string path, HttpStatusCode status, Uri target)
    {
        var targetPath = target.GetLeftPart(UriPartial.Path);
        var apiPath = "/api/" + path;
        var address = targetPath.EndsWith(apiPath, StringComparison.OrdinalIgnoreCase) ? targetPath[..^apiPath.Length] : "the address it redirects to";
        return new ImmichApiException(
            "ImmichRedirected",
            $"Immich at {_settings.BaseUrl} redirected {method} /api/{path} to {target} (HTTP {(int)status}). Set immich.baseUrl to {address}. VideoOptimiser doesn't follow redirects, so the API key goes only to the address you set.",
            (int)status);
    }

    /// <summary>
    /// Immich's error body carries a <c>message</c> that is a string, or a list for validation errors. It never echoes the
    /// key, so it is safe to show; anything unreadable is dropped.
    /// </summary>
    private static async Task<string> ErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("message", out var message)) return ".";
            var text = message.ValueKind switch
            {
                JsonValueKind.String => message.GetString(),
                JsonValueKind.Array => string.Join("; ", message.EnumerateArray().Select(item => item.ToString())),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(text)) return ".";
            return ": " + (text.Length > 300 ? text[..300] + "…" : text);
        }
        catch (Exception exception) when (exception is JsonException or IOException or HttpRequestException)
        {
            return ".";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string ApiKey() => _apiKey ??= SecretLoader.Load(_settings.ApiKeyEnvironmentVariable, _settings.ApiKeyFile)
        ?? throw new ImmichApiException("ImmichApiKeyMissing", $"Immich API key '{_settings.ApiKeyEnvironmentVariable}' was not found in the environment or '{_settings.ApiKeyFile}'. Add it to the ignored key file or set the environment variable.");
}
