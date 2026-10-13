using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Infrastructure.Immich;

namespace VideoOptimiser.UnitTests;

public sealed class ImmichHttpClientTests : IDisposable
{
    private const string AssetJson = """
        {"id":"asset-1","checksum":"Cf2Kr8LyQ1Yb9xeeG5jReVIp6v8=","originalFileName":"PXL_1.mp4","type":"VIDEO","visibility":"timeline",
         "isFavorite":false,"isTrashed":false,"libraryId":null,"livePhotoVideoId":null,
         "fileCreatedAt":"2025-05-28T23:51:45.000Z","fileModifiedAt":"2025-05-28T23:51:46.000Z","localDateTime":"2025-05-28T23:51:45.000Z",
         "duration":8000,"exifInfo":{"fileSizeInByte":20003484,"dateTimeOriginal":"2025-05-28T23:51:45+00:00","latitude":51.5,"longitude":-0.12,
         "description":"","rating":4,"make":"Google","model":"Pixel 9 Pro"},"tags":[{"id":"tag-1"},{"id":"tag-2"}]}
        """;

    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    // Unique per test: environment variables are process-wide and tests run in parallel.
    private readonly string _variable = $"VIDEO_OPTIMISER_TEST_IMMICH_KEY_{Guid.NewGuid():N}";

    public ImmichHttpClientTests()
    {
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable(_variable, "test-key");
    }

    [Theory]
    [InlineData("http://192.168.1.113:2283")]
    [InlineData("http://192.168.1.113:2283/api/")]
    public async Task GetServerVersionCallsTheApiPathWithoutAKey(string baseUrl)
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, """{"major":3,"minor":2,"patch":2}""");

        var version = await CreateClient(handler, baseUrl).GetServerVersionAsync();

        version.Should().Be(new ImmichServerVersion(3, 2, 2));
        handler.Last.Method.Should().Be(HttpMethod.Get);
        handler.Last.Uri.Should().Be(new Uri("http://192.168.1.113:2283/api/server/version"));
        handler.Last.ApiKey.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentUserSendsTheKeyFromTheEnvironmentInPreferenceToTheFile()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, ".env"), $"{_variable}=from-file\n");
        Environment.SetEnvironmentVariable(_variable, "from-environment");
        var handler = FakeHandler.Json(HttpStatusCode.OK, """{"id":"user-1","name":"Pat","email":"pat@example.com"}""");

        var user = await CreateClient(handler).GetCurrentUserAsync();

        user.Should().Be(new ImmichUser("user-1", "Pat"));
        handler.Last.Uri.AbsolutePath.Should().Be("/api/users/me");
        handler.Last.ApiKey.Should().Be("from-environment");
    }

    [Fact]
    public async Task GetCurrentUserFallsBackToTheKeyFile()
    {
        Environment.SetEnvironmentVariable(_variable, null);
        await File.WriteAllTextAsync(Path.Combine(_directory, ".env"), $"{_variable}=from-file\n");
        var handler = FakeHandler.Json(HttpStatusCode.OK, """{"id":"user-1","name":"Pat"}""");

        await CreateClient(handler).GetCurrentUserAsync();

        handler.Last.ApiKey.Should().Be("from-file");
    }

    [Fact]
    public async Task AMissingKeyNamesTheVariableAndFileWithoutSendingARequest()
    {
        Environment.SetEnvironmentVariable(_variable, null);
        var handler = FakeHandler.Json(HttpStatusCode.OK, "{}");

        var call = () => CreateClient(handler).GetCurrentUserAsync();

        var exception = (await call.Should().ThrowAsync<ImmichApiException>()).Which;
        exception.Code.Should().Be("ImmichApiKeyMissing");
        exception.Message.Should().Contain(_variable).And.Contain(".env");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "ImmichApiKeyRejected", "HTTP 401")]
    [InlineData(HttpStatusCode.Forbidden, "ImmichPermissionMissing", "user.read")]
    [InlineData(HttpStatusCode.InternalServerError, "ImmichRequestFailed", "HTTP 500")]
    public async Task ErrorResponsesBecomeMessagesThatSayWhatToFixAndNeverContainTheKey(HttpStatusCode status, string code, string expectedText)
    {
        Environment.SetEnvironmentVariable(_variable, "secret-key-value");

        var call = () => CreateClient(FakeHandler.Json(status, """{"message":"Missing required permission: user.read"}""")).GetCurrentUserAsync();

        var exception = (await call.Should().ThrowAsync<ImmichApiException>()).Which;
        exception.Code.Should().Be(code);
        exception.Message.Should().Contain(expectedText).And.NotContain("secret-key-value");
    }

    [Theory]
    [InlineData("search", "asset.read")]
    [InlineData("get", "asset.read")]
    [InlineData("download", "asset.download")]
    [InlineData("upload", "asset.upload")]
    [InlineData("update", "asset.update")]
    [InlineData("copy", "asset.copy")]
    [InlineData("tag", "tag.asset")]
    [InlineData("trash", "asset.delete")]
    [InlineData("check", "asset.upload")]
    public async Task AForbiddenCallNamesThePermissionItNeeds(string operation, string permission)
    {
        var client = CreateClient(FakeHandler.WithTrash(true, 30, _ => FakeHandler.JsonResponse(HttpStatusCode.Forbidden, """{"message":"Forbidden"}""")));
        var upload = await WriteFileAsync("upload.mp4", "av1");

        Func<Task> call = operation switch
        {
            "search" => async () => await client.SearchVideosAsync(DateTimeOffset.UtcNow, includeFavorites: true).ToListAsync(),
            "get" => () => client.GetAssetAsync("asset-1"),
            "download" => () => client.DownloadOriginalAsync("asset-1", Path.Combine(_directory, "original.mp4")),
            "upload" => () => client.UploadAsync(new ImmichUpload(upload, "upload.mp4", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false)),
            "update" => () => client.UpdateMetadataAsync("asset-1", new ImmichMetadataUpdate(Rating: 1)),
            "copy" => () => client.CopyRelationsAsync("asset-1", "asset-2"),
            "tag" => () => client.TagAssetAsync("asset-2", ["tag-1"]),
            "trash" => () => client.TrashAsync(["asset-1"]),
            _ => () => client.CheckUploadsAsync(["checksum"])
        };

        var exception = (await call.Should().ThrowAsync<ImmichApiException>()).Which;
        exception.Code.Should().Be("ImmichPermissionMissing");
        exception.Message.Should().Contain(permission);
    }

    [Fact]
    public async Task ARequestFailureIncludesImmichsValidationMessages()
    {
        var handler = FakeHandler.WithTrash(true, 30, _ => FakeHandler.JsonResponse(HttpStatusCode.BadRequest, """{"message":["ids must be an array","force must be a boolean"],"statusCode":400}"""));

        var call = () => CreateClient(handler).TrashAsync(["asset-1"]);

        (await call.Should().ThrowAsync<ImmichApiException>()).Which.Message
            .Should().Be("Immich returned HTTP 400 for DELETE /api/assets: ids must be an array; force must be a boolean");
    }

    [Fact]
    public async Task AConnectionFailureNamesTheBaseUrl()
    {
        var call = () => CreateClient(new FakeHandler(_ => throw new HttpRequestException("No route to host"))).GetServerVersionAsync();

        var exception = (await call.Should().ThrowAsync<ImmichApiException>()).Which;
        exception.Code.Should().Be("ImmichUnavailable");
        exception.Message.Should().Contain("http://192.168.1.113:2283");
    }

    [Fact]
    public async Task SearchVideosSendsTheStructuredFilterAndFollowsTheCursorToTheEnd()
    {
        var pages = new Queue<string>([
            $$$"""{"assets":{"items":[{{{AssetJson}}}],"nextCursor":"cursor-2","nextPage":null}}""",
            """{"assets":{"items":[{"id":"asset-2","checksum":"c2","originalFileName":"b.mp4","type":"VIDEO","visibility":"archive","fileCreatedAt":"2025-06-01T00:00:00Z","fileModifiedAt":"2025-06-01T00:00:00Z","localDateTime":"2025-06-01T00:00:00Z","duration":null}],"nextCursor":null}}"""]);
        var handler = new FakeHandler(_ => FakeHandler.JsonResponse(HttpStatusCode.OK, pages.Dequeue()));

        var assets = await CreateClient(handler).SearchVideosAsync(new DateTimeOffset(2026, 7, 14, 1, 0, 0, TimeSpan.FromHours(1)), includeFavorites: false).ToListAsync();

        assets.Select(asset => asset.Id).Should().Equal("asset-1", "asset-2");
        handler.Requests.Should().HaveCount(2).And.OnlyContain(request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/api/search/metadata");
        var first = JsonNode.Parse(handler.Requests[0].Body)!;
        first["filter"]!.ToJsonString(Unescaped).Should().Be(
            """{"type":{"eq":"VIDEO"},"takenAt":{"lt":"2026-07-14T00:00:00+00:00"},"visibility":{"in":["timeline","archive"]},"libraryId":{"eq":null},"trashedAt":{"eq":null},"isFavorite":{"eq":false}}""");
        first["withExif"]!.GetValue<bool>().Should().BeTrue();
        first["orderBy"]!.ToJsonString().Should().Be("""{"field":"fileCreatedAt","direction":"asc"}""");
        first.AsObject().ContainsKey("cursor").Should().BeFalse();
        JsonNode.Parse(handler.Requests[1].Body)!["cursor"]!.GetValue<string>().Should().Be("cursor-2");
    }

    [Fact]
    public async Task SearchVideosLeavesOutTheFavouriteFilterWhenFavouritesAreIncluded()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, """{"assets":{"items":[],"nextCursor":null}}""");

        await CreateClient(handler).SearchVideosAsync(DateTimeOffset.UtcNow, includeFavorites: true).ToListAsync();

        JsonNode.Parse(handler.Last.Body)!["filter"]!.AsObject().ContainsKey("isFavorite").Should().BeFalse();
    }

    [Fact]
    public async Task SearchVideosStopsWhenImmichRepeatsACursor()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, """{"assets":{"items":[],"nextCursor":"same"}}""");

        var call = async () => await CreateClient(handler).SearchVideosAsync(DateTimeOffset.UtcNow, includeFavorites: true).ToListAsync();

        (await call.Should().ThrowAsync<ImmichApiException>()).Which.Code.Should().Be("ImmichRequestFailed");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAssetMapsTheFieldsTheImmichSourceReads()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, AssetJson);

        var asset = await CreateClient(handler).GetAssetAsync("asset-1");

        handler.Last.Method.Should().Be(HttpMethod.Get);
        handler.Last.Uri.AbsolutePath.Should().Be("/api/assets/asset-1");
        handler.Last.ApiKey.Should().Be("test-key");
        asset.Should().BeEquivalentTo(new ImmichAsset(
            "asset-1", "Cf2Kr8LyQ1Yb9xeeG5jReVIp6v8=", "PXL_1.mp4", "VIDEO", "timeline", false, false, null, null,
            new DateTimeOffset(2025, 5, 28, 23, 51, 45, TimeSpan.Zero), new DateTimeOffset(2025, 5, 28, 23, 51, 46, TimeSpan.Zero),
            new DateTimeOffset(2025, 5, 28, 23, 51, 45, TimeSpan.Zero), 8000,
            new ImmichExif(20003484, new DateTimeOffset(2025, 5, 28, 23, 51, 45, TimeSpan.Zero), 51.5, -0.12, "", 4, "Google", "Pixel 9 Pro"),
            ["tag-1", "tag-2"]));
    }

    [Fact]
    public async Task DownloadOriginalStreamsToTheDestinationAndReportsProgress()
    {
        var bytes = Enumerable.Range(0, 3_000_000).Select(index => (byte)index).ToArray();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var destination = Path.Combine(_directory, "original.mp4");
        var progress = new RecordingProgress();

        await CreateClient(handler).DownloadOriginalAsync("asset-1", destination, progress);

        handler.Last.Uri.AbsolutePath.Should().Be("/api/assets/asset-1/original");
        (await File.ReadAllBytesAsync(destination)).Should().Equal(bytes);
        File.Exists(destination + ".part").Should().BeFalse();
        progress.Values.Should().NotBeEmpty().And.BeInAscendingOrder();
        progress.Values[^1].Should().Be(bytes.Length);
    }

    [Fact]
    public async Task ADownloadThatEndsEarlyLeavesNoFileUnderEitherName()
    {
        var handler = new FakeHandler(_ =>
        {
            var content = new StreamContent(new MemoryStream(new byte[10]));
            content.Headers.ContentLength = 100;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var destination = Path.Combine(_directory, "original.mp4");

        var call = () => CreateClient(handler).DownloadOriginalAsync("asset-1", destination);

        (await call.Should().ThrowAsync<ImmichApiException>()).Which.Code.Should().Be("ImmichUnavailable");
        File.Exists(destination).Should().BeFalse();
        File.Exists(destination + ".part").Should().BeFalse();
    }

    [Fact]
    public async Task AStalledDownloadTimesOutAndLeavesNoFile()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
        var destination = Path.Combine(_directory, "original.mp4");

        var call = () => CreateClient(handler, idleTimeout: TimeSpan.FromMilliseconds(200)).DownloadOriginalAsync("asset-1", destination);

        var exception = (await call.Should().ThrowAsync<ImmichApiException>()).Which;
        exception.Code.Should().Be("ImmichUnavailable");
        exception.Message.Should().Contain("did not respond");
        File.Exists(destination).Should().BeFalse();
        File.Exists(destination + ".part").Should().BeFalse();
    }

    [Fact]
    public async Task CancellingADownloadIsNotReportedAsATimeout()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var call = () => CreateClient(handler).DownloadOriginalAsync("asset-1", Path.Combine(_directory, "original.mp4"), cancellationToken: cancellation.Token);

        await call.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, "created", false)]
    [InlineData(HttpStatusCode.OK, "duplicate", true)]
    public async Task UploadSendsTheFileAndDatesAsMultipartAndMapsADuplicate(HttpStatusCode status, string immichStatus, bool duplicate)
    {
        var path = await WriteFileAsync("PXL_1.av1.mp4", "av1-bytes");
        var handler = FakeHandler.Json(status, $$"""{"id":"asset-9","status":"{{immichStatus}}"}""");
        var progress = new RecordingProgress();

        var result = await CreateClient(handler).UploadAsync(
            new ImmichUpload(path, "PXL_1.mp4", new DateTimeOffset(2025, 5, 29, 0, 51, 45, TimeSpan.FromHours(1)), new DateTimeOffset(2025, 5, 28, 23, 51, 46, TimeSpan.Zero), IsFavorite: true),
            progress);

        result.Should().Be(new ImmichUploadResult("asset-9", duplicate));
        var request = handler.Last;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsolutePath.Should().Be("/api/assets");
        request.ApiKey.Should().Be("test-key");
        request.ContentType.Should().StartWith("multipart/form-data");
        request.ContentLength.Should().NotBeNull("the body length is known up front, so it is not sent chunked");
        request.Body.Should().Contain("name=fileCreatedAt\r\n\r\n2025-05-28T23:51:45.000Z")
            .And.Contain("name=fileModifiedAt\r\n\r\n2025-05-28T23:51:46.000Z")
            .And.Contain("name=isFavorite\r\n\r\ntrue")
            .And.Contain("name=filename\r\n\r\nPXL_1.mp4")
            .And.Contain("name=assetData; filename=PXL_1.mp4")
            .And.Contain("av1-bytes");
        progress.Values[^1].Should().Be("av1-bytes".Length);
    }

    [Theory]
    [InlineData("Beach", 4, """{"description":"Beach","rating":4}""")]
    [InlineData(null, 5, """{"rating":5}""")]
    [InlineData("Beach", null, """{"description":"Beach"}""")]
    public async Task UpdateMetadataSendsOnlyTheFieldsToChange(string? description, int? rating, string expectedBody)
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, AssetJson);

        await CreateClient(handler).UpdateMetadataAsync("asset-9", new ImmichMetadataUpdate(Description: description, Rating: rating));

        handler.Last.Method.Should().Be(HttpMethod.Put);
        handler.Last.Uri.AbsolutePath.Should().Be("/api/assets/asset-9");
        handler.Last.Body.Should().Be(expectedBody);
    }

    [Fact]
    public async Task CopyRelationsCopiesEveryRelationFromSourceToTarget()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await CreateClient(handler).CopyRelationsAsync("asset-1", "asset-9");

        handler.Last.Method.Should().Be(HttpMethod.Put);
        handler.Last.Uri.AbsolutePath.Should().Be("/api/assets/copy");
        handler.Last.Body.Should().Be("""{"sourceId":"asset-1","targetId":"asset-9","albums":true,"favorite":true,"sharedLinks":true,"stack":true,"sidecar":true}""");
    }

    [Fact]
    public async Task TagAssetAddsTheTagsAndSkipsTheCallWhenThereAreNone()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, """{"count":2}""");
        var client = CreateClient(handler);

        await client.TagAssetAsync("asset-9", []);
        await client.TagAssetAsync("asset-9", ["tag-1", "tag-2"]);

        handler.Requests.Should().ContainSingle();
        handler.Last.Method.Should().Be(HttpMethod.Put);
        handler.Last.Uri.AbsolutePath.Should().Be("/api/tags/assets");
        handler.Last.Body.Should().Be("""{"assetIds":["asset-9"],"tagIds":["tag-1","tag-2"]}""");
    }

    [Fact]
    public async Task TrashMovesAssetsToTheTrashWithoutPurgingAfterReadingTheTrashSettingOnce()
    {
        var handler = FakeHandler.WithTrash(true, 30, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(handler);

        await client.TrashAsync(["asset-1"]);
        await client.TrashAsync(["asset-2"]);

        handler.Requests.Select(request => $"{request.Method} {request.Uri.AbsolutePath}").Should().Equal(
            "GET /api/server/features", "GET /api/server/config", "DELETE /api/assets", "DELETE /api/assets");
        handler.Requests.Take(2).Should().OnlyContain(request => request.ApiKey == null);
        handler.Requests[2].Body.Should().Be("""{"ids":["asset-1"],"force":false}""");
    }

    [Theory]
    [InlineData(false, 30)]
    [InlineData(true, 0)]
    public async Task TrashRefusesWithoutADeleteWhileImmichsTrashWouldPurge(bool enabled, int days)
    {
        var handler = FakeHandler.WithTrash(enabled, days, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(handler);

        var settings = await client.GetTrashSettingsAsync();
        var call = () => client.TrashAsync(["asset-1"]);

        settings.Should().Be(new ImmichTrashSettings(enabled, days));
        (await call.Should().ThrowAsync<ImmichApiException>()).Which.Code.Should().Be(ImmichTrash.DisabledCode);
        handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task CheckUploadsMapsResultsBackToTheirChecksumsAndReportsATrashedDuplicate()
    {
        // Results arrive out of order: the client matches them by the index it sent as each item's ID.
        var handler = FakeHandler.Json(HttpStatusCode.OK, """
            {"results":[{"id":"1","action":"accept"},
                        {"id":"0","action":"reject","reason":"duplicate","assetId":"asset-1","isTrashed":true}]}
            """);

        var results = await CreateClient(handler).CheckUploadsAsync(["sha-a", "sha-b"]);

        handler.Last.Method.Should().Be(HttpMethod.Post);
        handler.Last.Uri.AbsolutePath.Should().Be("/api/assets/bulk-upload-check");
        handler.Last.Body.Should().Be("""{"assets":[{"id":"0","checksum":"sha-a"},{"id":"1","checksum":"sha-b"}]}""");
        results.Should().Equal(
            new ImmichUploadCheck("sha-a", true, "duplicate", "asset-1", true),
            new ImmichUploadCheck("sha-b", false, null, null, false));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_variable, null);
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<string> WriteFileAsync(string name, string contents)
    {
        var path = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(path, contents);
        return path;
    }

    private ImmichHttpClient CreateClient(FakeHandler handler, string baseUrl = "http://192.168.1.113:2283", TimeSpan? idleTimeout = null) =>
        new(new HttpClient(handler),
            new ImmichSettings { BaseUrl = baseUrl, ApiKeyEnvironmentVariable = _variable, ApiKeyFile = Path.Combine(_directory, ".env") },
            requestTimeout: TimeSpan.FromSeconds(30),
            idleTimeout: idleTimeout ?? TimeSpan.FromSeconds(30));

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? ApiKey, string? ContentType, long? ContentLength, string Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        public RecordedRequest Last => Requests[^1];

        public static FakeHandler Json(HttpStatusCode status, string body) => new(_ => JsonResponse(status, body));

        /// <summary>Answers Immich's public trash setting endpoints, and every other request with <paramref name="respond"/>.</summary>
        public static FakeHandler WithTrash(bool enabled, int days, Func<HttpRequestMessage, HttpResponseMessage> respond) => new(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/server/features" => JsonResponse(HttpStatusCode.OK, $$"""{"trash":{{(enabled ? "true" : "false")}},"map":true}"""),
            "/api/server/config" => JsonResponse(HttpStatusCode.OK, $$"""{"trashDays":{{days}},"userDeleteDelay":7}"""),
            _ => respond(request)
        });

        public static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The length is read before the body, which would buffer it and make any length known.
            var length = request.Content?.Headers.ContentLength;
            // Read the body now: the client disposes the request content once the call returns.
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null,
                request.Content?.Headers.ContentType?.ToString(),
                length,
                body));
            return respond(request);
        }
    }

    private sealed class RecordingProgress : IProgress<long>
    {
        public List<long> Values { get; } = [];
        public void Report(long value) => Values.Add(value);
    }

    /// <summary>Sends a few bytes, then never another until cancelled, like a connection that has gone quiet.</summary>
    private sealed class StallingStream : Stream
    {
        private bool _sentFirst;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentFirst)
            {
                _sentFirst = true;
                buffer.Span[0] = 1;
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
