using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Configuration;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class RemoteSetupServiceTests
{
    // The MD5 of the three bytes "abc", which is what base64 "YWJj" decodes to.
    private const string PublicKeyLine = "ssh-ed25519 YWJj someone@laptop";
    private const string Fingerprint = "90:01:50:98:3c:d2:4f:b0:d6:96:3f:7d:28:e1:7f:72";

    [Fact]
    public async Task AMissingTokenPrintsTheConsoleStepsAndTheEnvLineWithoutCallingAnything()
    {
        using var fixture = new Fixture(token: false);

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.InvalidConfiguration);
        fixture.Output.Should().Contain($"{fixture.Variable}=<your token>");
        fixture.Output.Should().Contain(line => line.Contains("Security > API Tokens > Generate API token") && line.Contains("Read & Write"));
        fixture.Output.Should().Contain(line => line.Contains(Path.Combine(fixture.Directory, ".env")));
        fixture.Handler.Requests.Should().BeEmpty();
        fixture.Processes.Requests.Should().BeEmpty();
        fixture.ConfigShouldBeUntouched();
    }

    [Fact]
    public async Task ARejectedTokenStopsBeforeAnyChange()
    {
        using var fixture = new Fixture(identityFile: "keys/id_test");
        fixture.Handler.ListStatus = HttpStatusCode.Unauthorized;

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.InvalidConfiguration);
        fixture.Output.Should().Contain(line => line.Contains("rejected the API token") && line.Contains(fixture.Variable));
        fixture.Handler.Requests.Should().ContainSingle();
        fixture.ConfigShouldBeUntouched();
        fixture.TokenShouldNotBePrinted();
    }

    [Fact]
    public async Task AnUnreachableOrFailingHetznerIsAMissingDependency()
    {
        using var fixture = new Fixture(identityFile: "keys/id_test");
        fixture.Handler.ListStatus = HttpStatusCode.InternalServerError;

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.MissingDependency);
        fixture.Output.Should().Contain(line => line.Contains("500"));
        fixture.ConfigShouldBeUntouched();
    }

    [Fact]
    public async Task AKeyHetznerAlreadyHasIsReusedByFingerprintWithoutUploadOrKeygen()
    {
        using var fixture = new Fixture(identityFile: "keys/id_test");
        fixture.Handler.Keys.Add(new FakeKey("video-optimiser-hetzner-cx43", Fingerprint));

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.Success);
        fixture.Handler.Requests.Should().OnlyContain(request => request.Method == HttpMethod.Get);
        fixture.Handler.Requests[0].Authorization.Should().Be($"Bearer {fixture.Token}");
        fixture.Processes.Requests.Should().BeEmpty();
        fixture.Output.Should().Contain("Hetzner API token accepted.");
        fixture.Output.Should().Contain("Hetzner already has this key as \"video-optimiser-hetzner-cx43\".");
        fixture.Output.Should().Contain($"Updated {fixture.ConfigPath}; the previous version is in {fixture.ConfigPath}.bak.");
        fixture.Output.Should().Contain("Remote setup is complete. Nothing was created on Hetzner.");
        fixture.Output.Last().Should().Be($"Next: video-optimiser queue run --remote --config \"{fixture.ConfigPath}\"");
        var text = fixture.ReadConfig();
        text.Should().Contain("mode: \"remoteSsh\"").And.Contain("lifecycle: \"hetzner\"")
            .And.Contain("identityFile: \"keys/id_test\"")
            .And.Contain("sshKeyName: \"video-optimiser-hetzner-cx43\"")
            .And.Contain("serverTypes: [\"cx53\"]")
            .And.Contain("maxHourlyPrice: 0.5")
            .And.Contain("bootstrapScriptPath: \"bootstrap.sh\"");
        File.ReadAllBytes(fixture.ConfigPath + ".bak").Should().Equal(fixture.OriginalBytes);
        (await fixture.ReloadAndValidateAsync()).Should().BeEmpty();
        fixture.TokenShouldNotBePrinted();
    }

    [Fact]
    public async Task AKeyFoundOnALaterPageIsStillReused()
    {
        using var fixture = new Fixture(identityFile: "keys/id_test");
        fixture.Handler.PageSize = 1;
        fixture.Handler.Keys.Add(new FakeKey("one", "aa:aa"));
        fixture.Handler.Keys.Add(new FakeKey("two", "bb:bb"));
        fixture.Handler.Keys.Add(new FakeKey("mine", Fingerprint.ToUpperInvariant()));

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.Success);
        fixture.Handler.Requests.Select(request => request.Target).Should().Equal(
            "/v1/ssh_keys?per_page=50&page=1",
            "/v1/ssh_keys?per_page=50&page=2",
            "/v1/ssh_keys?per_page=50&page=3");
        fixture.ReadConfig().Should().Contain("sshKeyName: \"mine\"");
    }

    [Fact]
    public async Task ANewKeyIsUploadedWithoutItsCommentAndNamedInTheConfig()
    {
        using var fixture = new Fixture(identityFile: "keys/id_test");

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.Success);
        fixture.Handler.Posted.Should().ContainSingle().Which.Should().Be(("video-optimiser-hetzner", "ssh-ed25519 YWJj"));
        fixture.Output.Should().Contain("Uploaded the key to Hetzner as \"video-optimiser-hetzner\".");
        fixture.Output.Should().Contain("Remote setup is complete. Nothing was created on Hetzner except the SSH key.");
        fixture.ReadConfig().Should().Contain("sshKeyName: \"video-optimiser-hetzner\"");
    }

    [Theory]
    [InlineData("video-optimiser-hetzner", "video-optimiser-hetzner-2")]
    [InlineData("video-optimiser-hetzner,video-optimiser-hetzner-2", "video-optimiser-hetzner-3")]
    public async Task ANameTakenByADifferentKeyGetsTheNextFreeSuffix(string taken, string expected)
    {
        using var fixture = new Fixture(identityFile: "keys/id_test");
        foreach (var name in taken.Split(',')) fixture.Handler.Keys.Add(new FakeKey(name, $"ff:{name.Length}"));

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.Success);
        fixture.Handler.Posted.Should().ContainSingle().Which.Name.Should().Be(expected);
        fixture.ReadConfig().Should().Contain($"sshKeyName: \"{expected}\"");
    }

    [Fact]
    public async Task AReadOnlyTokenIsReportedAndLeavesTheConfigAlone()
    {
        using var fixture = new Fixture(identityFile: "keys/id_test");
        fixture.Handler.PostStatus = HttpStatusCode.Forbidden;

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.InvalidConfiguration);
        fixture.Output.Should().Contain(line => line.Contains("read-only") && line.Contains(Path.Combine(fixture.Directory, ".env")));
        fixture.ConfigShouldBeUntouched();
        fixture.TokenShouldNotBePrinted();
    }

    [Fact]
    public async Task WithNoIdentityFileTheDefaultKeyIsGeneratedByKeygenAndUploaded()
    {
        using var fixture = new Fixture();
        var keyPath = Path.Combine(fixture.Home, ".ssh", "video-optimiser-hetzner-ed25519");

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.Success);
        var request = fixture.Processes.Requests.Should().ContainSingle().Subject;
        request.FileName.Should().Be("ssh-keygen");
        request.Arguments.Should().Equal("-t", "ed25519", "-a", "100", "-N", "", "-C", "video-optimiser-hetzner", "-f", keyPath);
        fixture.Output.Should().Contain($"Created SSH key {keyPath}.");
        fixture.Handler.Posted.Should().ContainSingle();
        (await fixture.ReloadAsync()).Settings.Processing.RemoteSsh.IdentityFile.Should().Be(keyPath);
        (await fixture.ReloadAndValidateAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AnExistingDefaultKeyIsReusedWithoutRunningKeygen()
    {
        using var fixture = new Fixture();
        var keyPath = Fixture.WriteKey(Path.Combine(fixture.Home, ".ssh", "video-optimiser-hetzner-ed25519"));

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.Success);
        fixture.Processes.Requests.Should().BeEmpty();
        fixture.Output.Should().Contain($"Using SSH key {keyPath}.");
        (await fixture.ReloadAsync()).Settings.Processing.RemoteSsh.IdentityFile.Should().Be(keyPath);
    }

    [Fact]
    public async Task AConfiguredIdentityFileThatDoesNotExistFallsBackToTheDefaultKey()
    {
        using var fixture = new Fixture(identityFile: "keys/gone", createIdentity: false);

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.Success);
        fixture.Output.Should().Contain(line => line.Contains("does not exist; using the default key"));
        fixture.Processes.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task KeygenIsTakenFromBesideAConfiguredSshPath()
    {
        using var fixture = new Fixture(sshPath: "bin/ssh.exe");

        await fixture.RunAsync();

        var ssh = Path.GetFullPath(Path.Combine(fixture.Directory, "bin", "ssh.exe"));
        fixture.Processes.Requests.Should().ContainSingle().Which.FileName.Should().Be(Path.Combine(Path.GetDirectoryName(ssh)!, "ssh-keygen.exe"));
    }

    [Fact]
    public async Task AFailingKeygenIsAMissingDependencyAndNothingIsUploaded()
    {
        using var fixture = new Fixture();
        fixture.Processes.ExitCode = 1;
        fixture.Processes.StandardError = "boom: cannot write";

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.MissingDependency);
        fixture.Output.Should().Contain(line => line.Contains("boom: cannot write"));
        fixture.Handler.Posted.Should().BeEmpty();
        fixture.ConfigShouldBeUntouched();
    }

    [Fact]
    public async Task APrivateKeyWithoutAPublicKeyExplainsTheFix()
    {
        using var fixture = new Fixture(identityFile: "keys/id_test");
        File.Delete(Path.Combine(fixture.Directory, "keys", "id_test.pub"));

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.InvalidConfiguration);
        fixture.Output.Should().Contain(line => line.Contains("id_test.pub") && line.Contains("missing") && line.Contains("identityFile"));
        fixture.Handler.Posted.Should().BeEmpty();
        fixture.ConfigShouldBeUntouched();
    }

    [Fact]
    public async Task APublicKeyWithoutItsPrivateKeyIsNeverOverwritten()
    {
        using var fixture = new Fixture();
        var keyPath = Path.Combine(fixture.Home, ".ssh", "video-optimiser-hetzner-ed25519");
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllText(keyPath + ".pub", "precious");

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.InvalidConfiguration);
        fixture.Processes.Requests.Should().BeEmpty();
        File.ReadAllText(keyPath + ".pub").Should().Be("precious");
    }

    [Fact]
    public async Task EditingKeepsCommentsKeysOutsideTheSectionAndCrlfAndTheBom()
    {
        using var fixture = new Fixture(identityFile: "keys/id_test", crlf: true, bom: true);

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.Success);
        var bytes = File.ReadAllBytes(fixture.ConfigPath);
        bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble).Should().BeTrue();
        var text = fixture.ReadConfig();
        Regex.IsMatch(text, "(?<!\r)\n").Should().BeFalse("every line ending stays CRLF");
        text.Should().StartWith("# header comment\r\nversion: 1\r\n")
            .And.Contain("\r\n\r\n# database comment\r\ndatabase:\r\n  path: \"jobs.db\"\r\n")
            .And.Contain("watch:\r\n  roots:\r\n    - path: \"videos\"\r\n")
            .And.Contain("tools:\r\n  sshPath: \"ssh\"\r\n")
            .And.Contain("mode: \"remoteSsh\"");
        text.Should().NotContain("why local");
        (await fixture.ReloadAndValidateAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ALaterValidationFailureRestoresTheBackupAndExitsWithInvalidConfiguration()
    {
        using var fixture = new Fixture(identityFile: "keys/id_test", bootstrap: "missing.sh", createBootstrap: false);
        fixture.Handler.Keys.Add(new FakeKey("existing", Fingerprint));

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.InvalidConfiguration);
        fixture.Output.Should().Contain(line => line.StartsWith("[HetznerBootstrapMissing]"));
        fixture.Output.Should().Contain($"Restored {fixture.ConfigPath} from {fixture.ConfigPath}.bak.");
        File.ReadAllBytes(fixture.ConfigPath).Should().Equal(fixture.OriginalBytes);
        File.ReadAllBytes(fixture.ConfigPath + ".bak").Should().Equal(fixture.OriginalBytes);
        fixture.Output.Should().NotContain(line => line.StartsWith("Updated "));
    }

    [Fact]
    public async Task ProblemsThatWereAlreadyThereDoNotBlockTheEdit()
    {
        using var fixture = new Fixture(identityFile: "keys/id_test", complete: false);
        fixture.Handler.Keys.Add(new FakeKey("existing", Fingerprint));
        (await fixture.ReloadAndValidateAsync()).Should().NotBeEmpty();

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.Success);
        fixture.ReadConfig().Should().Contain("lifecycle: \"hetzner\"");
    }

    [Fact]
    public async Task DecliningPrintsTheBlockAndChangesNothing()
    {
        using var fixture = new Fixture(identityFile: "keys/id_test");

        var exit = await fixture.RunAsync(answer: false);

        exit.Should().Be(ExitCode.Success);
        fixture.Prompts.Should().Equal($"Write this to {fixture.ConfigPath}? A backup goes to {fixture.ConfigPath}.bak. [y/N] ");
        fixture.Output.Should().Contain(block => block.StartsWith("processing:") && block.Contains("sshKeyName: \"video-optimiser-hetzner\"") && block.Contains("mode: \"remoteSsh\""));
        fixture.Output.Should().Contain($"Config not changed. To use it, paste the block above over the processing: section of {fixture.ConfigPath}.");
        fixture.ConfigShouldBeUntouched();
    }

    [Fact]
    public async Task ARerunAfterDecliningFindsTheUploadedKeyAndTheGeneratedKey()
    {
        using var fixture = new Fixture();
        await fixture.RunAsync(answer: false);
        fixture.Handler.Requests.Clear();
        fixture.Processes.Requests.Clear();
        fixture.Output.Clear();

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.Success);
        fixture.Processes.Requests.Should().BeEmpty();
        fixture.Handler.Posted.Should().ContainSingle("only the first run uploads");
        fixture.Output.Should().Contain("Hetzner already has this key as \"video-optimiser-hetzner\".");
    }

    [Fact]
    public async Task AManualLifecycleWithAHostIsLeftToTheManualDocs()
    {
        using var fixture = new Fixture(host: "my-server");

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.InvalidConfiguration);
        fixture.Output.Should().Equal("remote setup configures managed Hetzner workers only. For your own SSH host, see \"Manual SSH lifecycle\" in docs/remote-ssh.md.");
        fixture.Handler.Requests.Should().BeEmpty();
        fixture.ConfigShouldBeUntouched();
    }

    [Fact]
    public async Task ImmichConfigPointsTheNextStepAtImmichRun()
    {
        using var fixture = new Fixture(identityFile: "keys/id_test", extra: "immich:\n  baseUrl: \"http://192.168.1.2:2283\"\n");

        var exit = await fixture.RunAsync();

        exit.Should().Be(ExitCode.Success);
        fixture.Output.Last().Should().Be($"Next: video-optimiser immich run --first --remote --config \"{fixture.ConfigPath}\"");
    }

    [Fact]
    public async Task CancellingAtThePromptStopsWithoutWriting()
    {
        using var fixture = new Fixture(identityFile: "keys/id_test");

        var action = () => fixture.RunAsync(confirm: (_, _) => throw new OperationCanceledException());

        await action.Should().ThrowAsync<OperationCanceledException>();
        fixture.ConfigShouldBeUntouched();
    }

    [Theory]
    [InlineData("ssh-ed25519 YWJj", true)]
    [InlineData("# comment\n\nssh-rsa YWJj me@host\n", true)]
    [InlineData("ssh-ed25519", false)]
    [InlineData("ssh-ed25519 not*base64", false)]
    [InlineData("", false)]
    public void PublicKeyParsingNeedsATypeAndABase64Blob(string text, bool valid)
    {
        RemoteSetupService.TryParsePublicKey(text, out var publicKey, out var fingerprint).Should().Be(valid);
        if (!valid) return;
        publicKey.Should().EndWith(" YWJj");
        fingerprint.Should().Be(Fingerprint);
    }

    [Theory]
    [InlineData("ssh", "ssh-keygen")]
    [InlineData("", "ssh-keygen")]
    public void KeygenIsFoundOnPathWhenSshHasNoFolder(string sshPath, string expected) =>
        RemoteSetupService.SshKeygenPath(sshPath).Should().Be(expected);

    private sealed record FakeKey(string Name, string Fingerprint);

    private sealed record HetznerRequest(HttpMethod Method, string Target, string? Authorization);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.RemoteSetup.{Guid.NewGuid():N}");

        public Fixture(
            string? identityFile = null,
            bool createIdentity = true,
            bool token = true,
            string host = "",
            string bootstrap = "bootstrap.sh",
            bool createBootstrap = true,
            string sshPath = "ssh",
            string extra = "",
            bool crlf = false,
            bool bom = false,
            bool complete = true)
        {
            Directory = _root;
            Home = Path.Combine(_root, "home");
            System.IO.Directory.CreateDirectory(Home);
            // A fresh name per fixture, so the real environment can never supply the token and mask a missing one.
            Variable = $"VOPT_TEST_TOKEN_{Guid.NewGuid():N}";
            Token = $"hcloud-secret-{Guid.NewGuid():N}";
            ConfigPath = Path.Combine(_root, "video-optimiser.yaml");
            if (token) File.WriteAllText(Path.Combine(_root, ".env"), $"{Variable}={Token}\n");
            if (createBootstrap) File.WriteAllText(Path.Combine(_root, bootstrap), "#!/bin/bash\n");
            if (identityFile is not null && createIdentity) WriteKey(Path.Combine(_root, identityFile));

            var yaml = new StringBuilder("# header comment\nversion: 1\ntools:\n");
            var sshValue = sshPath == "ssh" ? sshPath : Path.Combine(_root, sshPath).Replace('\\', '/');
            yaml.Append(CultureInfo.InvariantCulture, $"  sshPath: \"{sshValue}\"\n");
            yaml.Append("processing:\n  # why local\n  mode: \"local\"\n  remoteSsh:\n    lifecycle: \"manual\"\n");
            yaml.Append(CultureInfo.InvariantCulture, $"    host: \"{host}\"\n");
            if (identityFile is not null) yaml.Append(CultureInfo.InvariantCulture, $"    identityFile: \"{identityFile}\"\n");
            yaml.Append(CultureInfo.InvariantCulture, $"    hetzner:\n      apiTokenEnvironmentVariable: \"{Variable}\"\n      apiTokenFile: \".env\"\n      bootstrapScriptPath: \"{bootstrap}\"\n      serverTypes: [\"cx53\"]\n      maxHourlyPrice: 0.5\n");
            yaml.Append("\n# database comment\ndatabase:\n  path: \"jobs.db\"\n");
            if (complete)
            {
                yaml.Append("watch:\n  roots:\n    - path: \"videos\"\n");
                yaml.Append("eligibility:\n  rules:\n    - codecs: [\"h264\"]\n      resolution: \"4k+\"\n      minimumVideoBitrate: \"20Mbps\"\n      minimumFileSize: \"2GiB\"\n");
            }
            yaml.Append(extra);
            var text = crlf ? yaml.ToString().Replace("\n", "\r\n") : yaml.ToString();
            File.WriteAllText(ConfigPath, text, new UTF8Encoding(bom));
            OriginalBytes = File.ReadAllBytes(ConfigPath);
        }

        public string Directory { get; }
        public string Home { get; }
        public string Variable { get; }
        public string Token { get; }
        public string ConfigPath { get; }
        public byte[] OriginalBytes { get; }
        public FakeHetzner Handler { get; } = new();
        public FakeKeygen Processes { get; } = new();
        public List<string> Output { get; } = [];
        public List<string> Prompts { get; } = [];

        public string ReadConfig() => File.ReadAllText(ConfigPath);

        public static string WriteKey(string privatePath)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
            File.WriteAllText(privatePath, "PRIVATE");
            File.WriteAllText(privatePath + ".pub", PublicKeyLine + "\n");
            return privatePath;
        }

        public Task<ExitCode> RunAsync(bool answer = true, Func<string, CancellationToken, Task<bool>>? confirm = null) => RunCoreAsync(confirm ?? ((prompt, _) =>
        {
            Prompts.Add(prompt);
            return Task.FromResult(answer);
        }));

        private async Task<ExitCode> RunCoreAsync(Func<string, CancellationToken, Task<bool>> confirm)
        {
            var loader = new YamlConfigurationLoader();
            var configuration = await loader.LoadAsync(ConfigPath);
            using var service = new RemoteSetupService(
                new HetznerSshKeyClient(new HttpClient(Handler) { BaseAddress = new Uri("https://api.test/v1/") }),
                Processes,
                loader,
                new SettingsValidator(),
                () => Home);
            return await service.RunAsync(configuration, confirm, new SynchronousProgress(Output.Add), CancellationToken.None);
        }

        public Task<LoadedConfiguration> ReloadAsync() => new YamlConfigurationLoader().LoadAsync(ConfigPath);

        public async Task<IReadOnlyList<string>> ReloadAndValidateAsync() =>
            new SettingsValidator().Validate((await ReloadAsync()).Settings).Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}").ToArray();

        public void ConfigShouldBeUntouched()
        {
            File.ReadAllBytes(ConfigPath).Should().Equal(OriginalBytes);
            File.Exists(ConfigPath + ".bak").Should().BeFalse();
        }

        public void TokenShouldNotBePrinted() => Output.Should().NotContain(line => line.Contains(Token));

        public void Dispose() => System.IO.Directory.Delete(_root, recursive: true);
    }

    // ssh-keygen stand-in: writes the key pair named by -f, as the real tool would.
    private sealed class FakeKeygen : IExternalProcessRunner
    {
        public List<ExternalProcessRequest> Requests { get; } = [];
        public int ExitCode { get; set; }
        public string StandardError { get; set; } = string.Empty;

        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (ExitCode != 0) return Task.FromResult(new ExternalProcessResult(ExitCode, string.Empty, StandardError));
            var keyPath = request.Arguments[request.Arguments.ToList().IndexOf("-f") + 1];
            File.WriteAllText(keyPath, "PRIVATE");
            File.WriteAllText(keyPath + ".pub", PublicKeyLine + "\n");
            return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class FakeHetzner : HttpMessageHandler
    {
        public List<FakeKey> Keys { get; } = [];
        public List<HetznerRequest> Requests { get; } = [];
        public List<(string Name, string PublicKey)> Posted { get; } = [];
        public HttpStatusCode ListStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode PostStatus { get; set; } = HttpStatusCode.Created;
        public int PageSize { get; set; } = 50;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var target = request.RequestUri!.PathAndQuery;
            Requests.Add(new HetznerRequest(request.Method, target, request.Headers.Authorization?.ToString()));
            if (request.Method == HttpMethod.Get && target.StartsWith("/v1/ssh_keys?", StringComparison.Ordinal))
                return ListStatus == HttpStatusCode.OK ? Json(HttpStatusCode.OK, List(int.Parse(Regex.Match(target, @"page=(\d+)$").Groups[1].Value, CultureInfo.InvariantCulture))) : Error(ListStatus);
            if (request.Method == HttpMethod.Post && target == "/v1/ssh_keys")
            {
                if (PostStatus != HttpStatusCode.Created) return Error(PostStatus);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var name = body.RootElement.GetProperty("name").GetString()!;
                Posted.Add((name, body.RootElement.GetProperty("public_key").GetString()!));
                Keys.Add(new FakeKey(name, Fingerprint));
                return Json(HttpStatusCode.Created, $"{{\"ssh_key\":{KeyJson(Keys[^1], Keys.Count)}}}");
            }
            return Error(HttpStatusCode.NotFound);
        }

        private string List(int page)
        {
            var slice = Keys.Skip((page - 1) * PageSize).Take(PageSize).Select((key, index) => KeyJson(key, (page - 1) * PageSize + index + 1));
            var next = page * PageSize < Keys.Count ? (page + 1).ToString(CultureInfo.InvariantCulture) : "null";
            return $"{{\"ssh_keys\":[{string.Join(",", slice)}],\"meta\":{{\"pagination\":{{\"page\":{page},\"per_page\":{PageSize},\"next_page\":{next}}}}}}}";
        }

        private static string KeyJson(FakeKey key, int id) => $"{{\"id\":{id},\"name\":\"{key.Name}\",\"fingerprint\":\"{key.Fingerprint}\",\"public_key\":\"ssh-ed25519 AAAA\",\"labels\":{{}}}}";

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Error(HttpStatusCode status) => Json(status, $"{{\"error\":{{\"code\":\"{status}\",\"message\":\"HTTP {(int)status} from the fake\"}}}}");
    }

    private sealed class SynchronousProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }
}
