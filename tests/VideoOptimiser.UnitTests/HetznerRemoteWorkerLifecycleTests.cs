using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class HetznerRemoteWorkerLifecycleTests
{
    [Fact]
    public void DotEnvReaderSupportsCommentsExportQuotesAndLastAssignment()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "# ignored\nOTHER=value\nexport VIDEO_OPTIMISER_HETZNER_TOKEN='first'\nVIDEO_OPTIMISER_HETZNER_TOKEN=second\n");

            HetznerRemoteWorkerLifecycle.ReadDotEnvValue(path, "VIDEO_OPTIMISER_HETZNER_TOKEN").Should().Be("second");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EnsureLoadsTokenFromDotEnvWhenEnvironmentVariableIsAbsent()
    {
        using var fixture = new Fixture();
        fixture.UseDotEnvToken();
        using var lifecycle = fixture.CreateLifecycle();

        var worker = await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        worker.ServerId.Should().Be(42);
    }

    [Fact]
    public async Task EnsureCreatesLabelledServerEnablesIpAutoDeleteAndConfiguresSsh()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle();

        var worker = await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        worker.ServerId.Should().Be(42);
        worker.Address.Should().Be("203.0.113.42");
        fixture.Settings.Processing.RemoteSsh.Host.Should().Be("203.0.113.42");
        fixture.Settings.Processing.RemoteSsh.User.Should().Be("videoopt");
        fixture.Settings.Processing.RemoteSsh.KnownHostsFile.Should().EndWith(".hetzner-42.known_hosts");
        fixture.Handler.CreatePayload.Should().NotBeNull();
        fixture.Handler.CreatePayload!.RootElement.GetProperty("labels").GetProperty("video-optimiser-managed").GetString().Should().Be("true");
        fixture.Handler.CreatePayload.RootElement.GetProperty("ssh_keys")[0].GetString().Should().Be("video-optimiser-hetzner-cx43");
        fixture.Handler.CreatePayload.RootElement.GetProperty("public_net").GetProperty("enable_ipv4").GetBoolean().Should().BeTrue();
        fixture.Handler.CreatePayload.RootElement.GetProperty("public_net").GetProperty("enable_ipv6").GetBoolean().Should().BeFalse();
        fixture.Handler.CreatePayload.RootElement.GetProperty("user_data").GetString().Should().Contain(".bootstrap-complete");
        fixture.Handler.AutoDeleteUpdated.Should().BeTrue();
        fixture.Processes.Requests.Should().HaveCount(2);
        fixture.Processes.Requests[0].Arguments.Should().Contain("StrictHostKeyChecking=accept-new");
        fixture.Processes.Requests[0].Arguments.Should().ContainInOrder("-o", "User=root");
        fixture.Processes.Requests[1].Arguments.Should().ContainInOrder("-o", "User=videoopt");
        fixture.Settings.Processing.RemoteSsh.User.Should().Be("videoopt");
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeTrue();
    }

    [Fact]
    public async Task EnsurePrefiltersUnavailableLocationsAndPreservesConfiguredOrder()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.Locations = ["hel1", "fsn1", "nbg1"];
        fixture.Handler.LocationAvailability["hel1"] = (false, true);
        fixture.Handler.LocationAvailability["fsn1"] = (true, false);
        fixture.Handler.LocationAvailability["nbg1"] = (true, true);
        using var lifecycle = fixture.CreateLifecycle();
        var messages = new List<string>();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add));

        fixture.Handler.CreateLocations.Should().ContainSingle().Which.Should().Be("fsn1");
        messages.Should().Contain("Checking Hetzner cx43 capacity in hel1, fsn1, or nbg1.");
        messages.Should().Contain("Hetzner cx43 capacity available in fsn1 or nbg1; trying configured order.");
        messages.Should().Contain("Creating Hetzner server in fsn1.");
        messages.Should().NotContain(message => message.Contains("recommended", StringComparison.OrdinalIgnoreCase));
        messages.Should().NotContain(message => message.Contains("available=False", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnsureFallsBackWhenAvailabilityPreflightIsStale()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.Locations = ["hel1", "fsn1"];
        fixture.Handler.CreateFailures.Enqueue((HttpStatusCode.PreconditionFailed, "resource_unavailable", "error during placement"));
        using var lifecycle = fixture.CreateLifecycle();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        fixture.Handler.CreateLocations.Should().Equal("hel1", "fsn1");
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeTrue();
    }

    [Theory]
    [InlineData("resource_unavailable")]
    [InlineData("placement_unavailable")]
    public async Task EnsureCleansUpAllocatedWorkerWhenProvisioningPlacementFails(string actionCode)
    {
        using var fixture = new Fixture { AutoDeletePrimaryIpWithServer = false };
        fixture.Settings.Processing.RemoteSsh.Hetzner.Locations = ["hel1", "fsn1"];
        fixture.Handler.ActionFailures.Enqueue((actionCode, "error during placement"));
        using var lifecycle = fixture.CreateLifecycle();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        fixture.Handler.CreateLocations.Should().Equal("hel1", "fsn1");
        fixture.Handler.SecondCreateObservedCleanResources.Should().BeTrue();
        fixture.Handler.ServerExists.Should().BeTrue();
        fixture.Handler.PrimaryIpExists.Should().BeTrue();
        fixture.Handler.PrimaryIpDeleted.Should().BeTrue();
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeTrue();
    }

    [Fact]
    public async Task EnsureReportsAllPlacementFailuresWhenCandidatesAreExhausted()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.Locations = ["hel1", "fsn1"];
        fixture.Handler.CreateFailures.Enqueue((HttpStatusCode.PreconditionFailed, "resource_unavailable", "hel capacity"));
        fixture.Handler.CreateFailures.Enqueue((HttpStatusCode.PreconditionFailed, "resource_unavailable", "fsn capacity"));
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        var exception = await action.Should().ThrowAsync<RemoteWorkerCapacityUnavailableException>();
        exception.Which.Message.Should().Be("No CX43 capacity is currently available in hel1 or fsn1. No server was created. Try again later.");
        exception.Which.AttemptedFailures.Should().HaveCount(2)
            .And.ContainInOrder("hel1 (HTTP 412 PreconditionFailed (resource_unavailable): hel capacity)", "fsn1 (HTTP 412 PreconditionFailed (resource_unavailable): fsn capacity)");
        fixture.Handler.CreateCount.Should().Be(2);
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeFalse();
    }

    [Fact]
    public async Task EnsureDoesNotRetryNonPlacementApiErrors()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.Locations = ["hel1", "fsn1"];
        fixture.Handler.CreateFailures.Enqueue((HttpStatusCode.BadRequest, "invalid_input", "bad request"));
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*400*bad request*");
        fixture.Handler.CreateCount.Should().Be(1);
    }

    [Fact]
    public async Task EnsureDoesNotCreateWhenPreflightReportsNoConfiguredAvailability()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.Locations = ["hel1", "fsn1"];
        fixture.Handler.LocationAvailability["hel1"] = (false, false);
        fixture.Handler.LocationAvailability["fsn1"] = (false, false);
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        var exception = await action.Should().ThrowAsync<RemoteWorkerCapacityUnavailableException>();
        exception.Which.Message.Should().Be("No CX43 capacity is currently available in hel1 or fsn1. No server was created. Try again later.");
        exception.Which.Locations.Should().Equal("hel1", "fsn1");
        fixture.Handler.CreateCount.Should().Be(0);
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeFalse();
    }

    [Fact]
    public async Task EnsureDoesNotCreateWhenServerTypePreflightResponseIsMissingServerTypes()
    {
        using var fixture = new Fixture();
        fixture.Handler.ServerTypePreflightResponse = "{}";
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*did not contain a server_types array*no server creation was attempted*");
        fixture.Handler.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureDoesNotCreateWhenServerTypeLocationPreflightIsMalformed()
    {
        using var fixture = new Fixture();
        fixture.Handler.ServerTypePreflightResponse = "{\"server_types\":[{\"name\":\"cx43\",\"locations\":[{\"name\":\"hel1\"}]}]}";
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*did not contain a boolean available field*no server creation was attempted*");
        fixture.Handler.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureReportsBootstrapStageAndPercentageFromRemoteProgressFile()
    {
        using var fixture = new Fixture();
        fixture.Processes.Results.Enqueue(new ExternalProcessResult(0, "percent=42\nstage=Building FFmpeg\n", string.Empty));
        fixture.Processes.Results.Enqueue(new ExternalProcessResult(0, "percent=100\nstage=Ready\n", string.Empty));
        using var lifecycle = fixture.CreateLifecycle();
        var messages = new List<string>();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add));

        messages.Should().Contain("Worker bootstrap 42% — Building FFmpeg.");
    }

    [Fact]
    public async Task EnsureReportsHeartbeatWhenBootstrapPhaseDoesNotChange()
    {
        using var fixture = new Fixture();
        for (var attempt = 0; attempt < 5; attempt++)
            fixture.Processes.Results.Enqueue(new ExternalProcessResult(0, "percent=60\nstage=Building FFmpeg\n", string.Empty));
        fixture.Processes.Results.Enqueue(new ExternalProcessResult(0, "percent=100\nstage=Ready\n", string.Empty));
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        using var lifecycle = fixture.CreateLifecycle(
            progressHeartbeatInterval: TimeSpan.FromSeconds(60),
            utcNow: clock.Now,
            delay: (_, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(15));
                return Task.CompletedTask;
            });
        var messages = new List<string>();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add));

        messages.Should().Contain("Worker bootstrap 60% — Building FFmpeg (elapsed 1m 0s).");
    }

    [Fact]
    public async Task FinalReadinessCheckUsesVideooptAfterRootReportsBootstrapComplete()
    {
        using var fixture = new Fixture();
        fixture.Processes.Results.Enqueue(new ExternalProcessResult(0, "percent=100\nstage=Ready\n", string.Empty));
        fixture.Processes.Results.Enqueue(new ExternalProcessResult(0, string.Empty, string.Empty));
        using var lifecycle = fixture.CreateLifecycle();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        fixture.Processes.Requests.Should().HaveCount(2);
        fixture.Processes.Requests[0].Arguments.Should().ContainInOrder("-o", "User=root");
        fixture.Processes.Requests[1].Arguments.Should().ContainInOrder("-o", "User=videoopt");
    }

    [Fact]
    public async Task ReadinessProbeTimeoutCancelsTheSshAttemptAndRetries()
    {
        using var fixture = new Fixture();
        fixture.Processes.BlockingAttempts = 1;
        using var lifecycle = fixture.CreateLifecycle(TimeSpan.FromMilliseconds(20));

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        fixture.Processes.Requests.Count.Should().Be(3);
        fixture.Processes.CancelledRequests.Should().Be(1);
    }

    [Fact]
    public async Task EnsureReusesPersistedOwnedServerWithoutCreatingAnother()
    {
        using var fixture = new Fixture();
        using (var first = fixture.CreateLifecycle()) await first.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        var createCount = fixture.Handler.CreateCount;

        using var second = fixture.CreateLifecycle();
        var worker = await second.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        worker.ServerId.Should().Be(42);
        fixture.Handler.CreateCount.Should().Be(createCount);
    }

    [Fact]
    public async Task EnsureRecoversOwnedLabelledServerWhenLocalStateWasLost()
    {
        using var fixture = new Fixture();
        using (var first = fixture.CreateLifecycle()) await first.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        var createCount = fixture.Handler.CreateCount;
        File.Delete(fixture.DatabasePath + ".hetzner-worker.json");

        using var second = fixture.CreateLifecycle();
        var worker = await second.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        worker.ServerId.Should().Be(42);
        fixture.Handler.CreateCount.Should().Be(createCount);
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeTrue();
    }

    [Fact]
    public async Task DeleteRemovesOwnedServerPrimaryIpAndLocalRecoveryState()
    {
        using var fixture = new Fixture { AutoDeletePrimaryIpWithServer = false };
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        var deleted = await lifecycle.DeleteAsync(fixture.Settings, fixture.DatabasePath);

        deleted.Should().BeTrue();
        fixture.Handler.ServerExists.Should().BeFalse();
        fixture.Handler.PrimaryIpExists.Should().BeFalse();
        fixture.Handler.PrimaryIpDeleted.Should().BeTrue();
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeFalse();
    }

    [Fact]
    public async Task DeleteFindsLabelledOrphanPrimaryIpAfterServerAndStateWereLost()
    {
        using var fixture = new Fixture { AutoDeletePrimaryIpWithServer = false };
        using (var first = fixture.CreateLifecycle()) await first.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        fixture.Handler.DeleteServerOutOfBand();
        File.Delete(fixture.DatabasePath + ".hetzner-worker.json");

        using var second = fixture.CreateLifecycle();
        var deleted = await second.DeleteAsync(fixture.Settings, fixture.DatabasePath);

        deleted.Should().BeTrue();
        fixture.Handler.PrimaryIpDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task RefusesToRecoverServerWithoutExactOwnershipLabels()
    {
        using var fixture = new Fixture();
        fixture.Handler.ReturnWrongOwnership = true;
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ownership labels*");
        fixture.Handler.CreatePayload.Should().BeNull();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "video-optimiser-tests", Guid.NewGuid().ToString("N"));
        private readonly string _tokenVariable = "VIDEO_OPTIMISER_TEST_TOKEN_" + Guid.NewGuid().ToString("N");

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "jobs.db");
            var bootstrap = Path.Combine(_directory, "bootstrap.sh");
            File.WriteAllText(bootstrap, "#!/usr/bin/env bash\ntouch /opt/video-optimiser/.bootstrap-complete\n");
            var identity = Path.Combine(_directory, "worker-key");
            File.WriteAllText(identity, "test");
            Settings = new AppSettings
            {
                Processing = new ProcessingSettings
                {
                    Mode = ProcessingModes.RemoteSsh,
                    RemoteSsh = new RemoteSshSettings
                    {
                        Lifecycle = RemoteLifecycleModes.Hetzner,
                        IdentityFile = identity,
                        Hetzner = new HetznerSettings
                        {
                            ApiTokenEnvironmentVariable = _tokenVariable,
                            SshKeyName = "video-optimiser-hetzner-cx43",
                            BootstrapScriptPath = bootstrap
                        }
                    }
                }
            };
            Environment.SetEnvironmentVariable(_tokenVariable, "secret-test-token");
        }

        public string DatabasePath { get; }
        public AppSettings Settings { get; }
        public FakeHetznerHandler Handler { get; } = new();
        public RecordingProcessRunner Processes { get; } = new();
        public bool AutoDeletePrimaryIpWithServer { set => Handler.AutoDeletePrimaryIpWithServer = value; }

        public void UseDotEnvToken()
        {
            Environment.SetEnvironmentVariable(_tokenVariable, null);
            var path = Path.Combine(_directory, ".env");
            File.WriteAllText(path, $"{_tokenVariable}=secret-test-token\n");
            Settings.Processing.RemoteSsh.Hetzner.ApiTokenFile = path;
        }

        public HetznerRemoteWorkerLifecycle CreateLifecycle(
            TimeSpan? readinessProbeTimeout = null,
            TimeSpan? progressHeartbeatInterval = null,
            Func<DateTimeOffset>? utcNow = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null) => new(
            Processes,
            new HttpClient(Handler) { BaseAddress = new Uri("https://api.test/v1/") },
            delay ?? ((_, _) => Task.CompletedTask),
            ownsHttpClient: true,
            readinessProbeTimeout: readinessProbeTimeout,
            progressHeartbeatInterval: progressHeartbeatInterval,
            utcNow: utcNow);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_tokenVariable, null);
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class RecordingProcessRunner : IExternalProcessRunner
    {
        public List<ExternalProcessRequest> Requests { get; } = [];
        public Queue<ExternalProcessResult> Results { get; } = new();
        public int BlockingAttempts { get; set; }
        public int CancelledRequests { get; private set; }

        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (BlockingAttempts > 0)
            {
                BlockingAttempts--;
                var completion = new TaskCompletionSource<ExternalProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() =>
                {
                    CancelledRequests++;
                    completion.TrySetCanceled(cancellationToken);
                });
                return completion.Task;
            }

            if (Results.TryDequeue(out var result)) return Task.FromResult(result);
            return Task.FromResult(new ExternalProcessResult(0, "percent=100\nstage=Ready\n", string.Empty));
        }
    }

    private sealed class SynchronousProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }

    private sealed class FakeClock(DateTimeOffset initial)
    {
        private DateTimeOffset _now = initial;

        public DateTimeOffset Now() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class FakeHetznerHandler : HttpMessageHandler
    {
        public bool ServerExists { get; private set; }
        public bool PrimaryIpExists { get; private set; }
        public bool PrimaryIpDeleted { get; private set; }
        public bool AutoDeleteUpdated { get; private set; }
        public bool AutoDeletePrimaryIpWithServer { get; set; } = true;
        public bool ReturnWrongOwnership { get; set; }
        public string? ServerTypePreflightResponse { get; set; }
        public Queue<(string Code, string Message)> ActionFailures { get; } = new();
        public bool SecondCreateObservedCleanResources { get; private set; }
        private (string Code, string Message)? _pendingActionFailure;
        public JsonDocument? CreatePayload { get; set; }
        public int CreateCount { get; private set; }
        public Dictionary<string, (bool Available, bool Recommended)> LocationAvailability { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hel1"] = (true, false),
            ["fsn1"] = (true, false),
            ["nbg1"] = (true, false)
        };
        public Queue<(HttpStatusCode Status, string Code, string Message)> CreateFailures { get; } = new();
        public List<string> CreateLocations { get; } = [];

        public void DeleteServerOutOfBand() => ServerExists = false;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("secret-test-token");
            var path = request.RequestUri!.PathAndQuery;
            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/server_types?name=", StringComparison.Ordinal))
            {
                if (ServerTypePreflightResponse is not null) return Json(ServerTypePreflightResponse);
                var locations = string.Join(",", LocationAvailability.Select(item => $"{{\"name\":\"{item.Key}\",\"available\":{item.Value.Available.ToString().ToLowerInvariant()},\"recommended\":{item.Value.Recommended.ToString().ToLowerInvariant()}}}"));
                return Json($"{{\"server_types\":[{{\"name\":\"cx43\",\"locations\":[{locations}]}}]}}");
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/servers?", StringComparison.Ordinal))
            {
                var servers = ReturnWrongOwnership ? $"[{ServerJson(wrongOwnership: true)}]" : ServerExists ? $"[{ServerJson()}]" : "[]";
                return Json($"{{\"servers\":{servers}}}");
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/primary_ips?", StringComparison.Ordinal))
                return Json(PrimaryIpExists ? "{\"primary_ips\":[{\"id\":99}]}" : "{\"primary_ips\":[]}");
            if (request.Method == HttpMethod.Post && path == "/v1/servers")
            {
                CreatePayload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (CreateCount == 1) SecondCreateObservedCleanResources = !ServerExists && !PrimaryIpExists;
                CreateCount++;
                CreateLocations.Add(CreatePayload.RootElement.GetProperty("location").GetString()!);
                if (CreateFailures.TryDequeue(out var failure))
                    return Error(failure.Status, failure.Code, failure.Message);
                ServerExists = true;
                PrimaryIpExists = true;
                if (ActionFailures.TryDequeue(out var actionFailure))
                {
                    _pendingActionFailure = actionFailure;
                    return Json($"{{\"server\":{ServerJson()},\"action\":{{\"id\":12}}}}");
                }
                return Json($"{{\"server\":{ServerJson()},\"action\":{{\"id\":10}}}}");
            }
            if (request.Method == HttpMethod.Put && path == "/v1/primary_ips/99")
            {
                AutoDeleteUpdated = true;
                return Json("{\"primary_ip\":{\"id\":99}}");
            }
            if (request.Method == HttpMethod.Get && path == "/v1/actions/12" && _pendingActionFailure is { } pendingFailure)
                return Json($"{{\"action\":{{\"status\":\"error\",\"error\":{{\"code\":\"{pendingFailure.Code}\",\"message\":\"{pendingFailure.Message}\"}}}}}}");
            if (request.Method == HttpMethod.Get && path is "/v1/actions/10" or "/v1/actions/11")
                return Json("{\"action\":{\"status\":\"success\"}}");
            if (request.Method == HttpMethod.Get && path == "/v1/servers/42")
                return ServerExists ? Json($"{{\"server\":{ServerJson()}}}") : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Delete && path == "/v1/servers/42")
            {
                ServerExists = false;
                if (AutoDeletePrimaryIpWithServer) PrimaryIpExists = false;
                return Json("{\"action\":{\"id\":11}}");
            }
            if (request.Method == HttpMethod.Get && path == "/v1/primary_ips/99")
                return PrimaryIpExists ? Json("{\"primary_ip\":{\"id\":99}}") : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Delete && path == "/v1/primary_ips/99")
            {
                PrimaryIpExists = false;
                PrimaryIpDeleted = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        }

        private string ServerJson(bool wrongOwnership = false)
        {
            var labels = wrongOwnership
                ? "\"video-optimiser-managed\":\"true\",\"video-optimiser-controller\":\"someone-else\""
                : CreatePayload?.RootElement.GetProperty("labels").EnumerateObject().Select(item => $"\"{item.Name}\":\"{item.Value.GetString()}\"").Aggregate((left, right) => left + "," + right)
                  ?? "\"video-optimiser-managed\":\"true\",\"video-optimiser-controller\":\"placeholder\"";
            return $"{{\"id\":42,\"name\":\"video-optimiser-test\",\"status\":\"running\",\"created\":\"2026-08-20T00:00:00Z\",\"labels\":{{{labels}}},\"public_net\":{{\"ipv4\":{{\"id\":99,\"ip\":\"203.0.113.42\"}}}}}}";
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) => new(status)
        {
            Content = new StringContent($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}", Encoding.UTF8, "application/json")
        };
    }
}
