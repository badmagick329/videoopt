using System.Net;
using System.Text;
using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Infrastructure.Immich;

namespace VideoOptimiser.UnitTests;

public sealed class ImmichHttpClientTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    // Unique per test: environment variables are process-wide and tests run in parallel.
    private readonly string _variable = $"VIDEO_OPTIMISER_TEST_IMMICH_KEY_{Guid.NewGuid():N}";

    public ImmichHttpClientTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("http://192.168.1.113:2283")]
    [InlineData("http://192.168.1.113:2283/")]
    [InlineData("http://192.168.1.113:2283/api/")]
    public async Task GetServerVersionCallsTheApiPathWithoutAKey(string baseUrl)
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"major":3,"minor":2,"patch":2}""");

        var version = await CreateClient(handler, baseUrl).GetServerVersionAsync();

        version.Should().Be(new ImmichServerVersion(3, 2, 2));
        handler.Request!.Method.Should().Be(HttpMethod.Get);
        handler.Request.RequestUri.Should().Be(new Uri("http://192.168.1.113:2283/api/server/version"));
        handler.Request.Headers.Contains("x-api-key").Should().BeFalse();
    }

    [Fact]
    public async Task GetCurrentUserSendsTheKeyFromTheEnvironmentInPreferenceToTheFile()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, ".env"), $"{_variable}=from-file\n");
        Environment.SetEnvironmentVariable(_variable, "from-environment");
        var handler = new FakeHandler(HttpStatusCode.OK, """{"id":"user-1","name":"Pat","email":"pat@example.com"}""");

        var user = await CreateClient(handler).GetCurrentUserAsync();

        user.Should().Be(new ImmichUser("user-1", "Pat"));
        handler.Request!.RequestUri!.AbsolutePath.Should().Be("/api/users/me");
        handler.Request.Headers.GetValues("x-api-key").Should().Equal("from-environment");
    }

    [Fact]
    public async Task GetCurrentUserFallsBackToTheKeyFile()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, ".env"), $"{_variable}=from-file\n");
        var handler = new FakeHandler(HttpStatusCode.OK, """{"id":"user-1","name":"Pat"}""");

        await CreateClient(handler).GetCurrentUserAsync();

        handler.Request!.Headers.GetValues("x-api-key").Should().Equal("from-file");
    }

    [Fact]
    public async Task AMissingKeyNamesTheVariableAndFileWithoutSendingARequest()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");

        var call = () => CreateClient(handler).GetCurrentUserAsync();

        var exception = (await call.Should().ThrowAsync<ImmichApiException>()).Which;
        exception.Code.Should().Be("ImmichApiKeyMissing");
        exception.Message.Should().Contain(_variable).And.Contain(".env");
        handler.Request.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "ImmichApiKeyRejected", "HTTP 401")]
    [InlineData(HttpStatusCode.Forbidden, "ImmichPermissionMissing", "user.read")]
    [InlineData(HttpStatusCode.InternalServerError, "ImmichRequestFailed", "HTTP 500")]
    public async Task ErrorResponsesBecomeMessagesThatSayWhatToFixAndNeverContainTheKey(HttpStatusCode status, string code, string expectedText)
    {
        Environment.SetEnvironmentVariable(_variable, "secret-key-value");

        var call = () => CreateClient(new FakeHandler(status, """{"message":"Missing required permission: user.read"}""")).GetCurrentUserAsync();

        var exception = (await call.Should().ThrowAsync<ImmichApiException>()).Which;
        exception.Code.Should().Be(code);
        exception.Message.Should().Contain(expectedText).And.NotContain("secret-key-value");
    }

    [Fact]
    public async Task AConnectionFailureNamesTheBaseUrl()
    {
        var call = () => CreateClient(new FakeHandler(new HttpRequestException("No route to host"))).GetServerVersionAsync();

        var exception = (await call.Should().ThrowAsync<ImmichApiException>()).Which;
        exception.Code.Should().Be("ImmichUnavailable");
        exception.Message.Should().Contain("http://192.168.1.113:2283");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_variable, null);
        Directory.Delete(_directory, recursive: true);
    }

    private ImmichHttpClient CreateClient(FakeHandler handler, string baseUrl = "http://192.168.1.113:2283") =>
        new(new HttpClient(handler), new ImmichSettings { BaseUrl = baseUrl, ApiKeyEnvironmentVariable = _variable, ApiKeyFile = Path.Combine(_directory, ".env") });

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body = string.Empty;
        private readonly Exception? _failure;

        public FakeHandler(HttpStatusCode status, string body) => (_status, _body) = (status, body);
        public FakeHandler(Exception failure) => _failure = failure;

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (_failure is not null) throw _failure;
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
        }
    }
}
