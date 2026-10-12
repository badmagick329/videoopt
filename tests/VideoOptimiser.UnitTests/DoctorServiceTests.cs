using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Configuration;
using VideoOptimiser.Infrastructure.Diagnostics;

namespace VideoOptimiser.UnitTests;

public sealed class DoctorServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public DoctorServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task RunAsyncReportsSuccessWhenConfigurationAndToolsAreHealthy()
    {
        var settings = CreateSettings();
        var report = await CreateDoctor(new FakeToolVerifier(true)).RunAsync(new LoadedConfiguration(settings, Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.Success);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "DependencyAvailable");
    }

    [Fact]
    public async Task RunAsyncReturnsMissingDependencyExitCodeWhenAToolFails()
    {
        var settings = CreateSettings();
        var report = await CreateDoctor(new FakeToolVerifier(false)).RunAsync(new LoadedConfiguration(settings, Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.MissingDependency);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Category == DiagnosticCategory.Dependency && diagnostic.Status == DiagnosticStatus.Fail);
    }

    [Fact]
    public async Task RunAsyncInRemoteModeChecksSshAndSftpAndSkipsLocalAbAv1()
    {
        var settings = CreateSettings();
        settings.Processing.Mode = ProcessingModes.RemoteSsh;
        settings.Processing.RemoteSsh.Host = "video-worker";
        var tools = new FakeToolVerifier(true);
        var remote = new FakeRemoteEnvironmentVerifier();

        var report = await CreateDoctor(tools, remote).RunAsync(new LoadedConfiguration(settings, Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.Success);
        tools.VerifiedNames.Should().BeEquivalentTo(["ffmpeg", "ffprobe", "ssh", "sftp"]);
        remote.WasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsyncDoesNotConnectRemotelyWhenLocalOpenSshIsUnavailable()
    {
        var settings = CreateSettings();
        settings.Processing.Mode = ProcessingModes.RemoteSsh;
        settings.Processing.RemoteSsh.Host = "video-worker";
        var tools = new FakeToolVerifier(true, unavailableName: "ssh");
        var remote = new FakeRemoteEnvironmentVerifier();

        var report = await CreateDoctor(tools, remote).RunAsync(new LoadedConfiguration(settings, Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.MissingDependency);
        remote.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsyncUnderHetznerWithoutAWorkerChecksTheApiAndSkipsHostChecksWithoutFailing()
    {
        var settings = CreateHetznerSettings();
        var remote = new FakeRemoteEnvironmentVerifier();

        var report = await CreateDoctor(new FakeToolVerifier(true), remote, new FakeRemoteWorkerLifecycle(worker: null)).RunAsync(new LoadedConfiguration(settings, Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.Success);
        report.Diagnostics.Should().NotContain(diagnostic => diagnostic.Status == DiagnosticStatus.Fail);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "HetznerApiAvailable" && diagnostic.Status == DiagnosticStatus.Pass);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "RemoteHostChecksSkipped" && diagnostic.Status == DiagnosticStatus.Warning);
        remote.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsyncUnderHetznerChecksTheRunningWorker()
    {
        var settings = CreateHetznerSettings();
        var remote = new FakeRemoteEnvironmentVerifier();
        var worker = new RemoteWorkerInfo(42, "video-optimiser-1", "203.0.113.7", "running", null);

        var report = await CreateDoctor(new FakeToolVerifier(true), remote, new FakeRemoteWorkerLifecycle(worker)).RunAsync(new LoadedConfiguration(settings, Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.Success);
        remote.CheckedHost.Should().Be("203.0.113.7");
        report.Diagnostics.Should().NotContain(diagnostic => diagnostic.Code == "RemoteHostChecksSkipped");
    }

    [Fact]
    public async Task RunAsyncUnderHetznerFailsWhenTheApiCheckFails()
    {
        var settings = CreateHetznerSettings();
        var remote = new FakeRemoteEnvironmentVerifier();
        var lifecycle = new FakeRemoteWorkerLifecycle(worker: null, failure: new InvalidOperationException("Hetzner API token 'VIDEO_OPTIMISER_HETZNER_TOKEN' was not found."));

        var report = await CreateDoctor(new FakeToolVerifier(true), remote, lifecycle).RunAsync(new LoadedConfiguration(settings, Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.MissingDependency);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "HetznerApiUnavailable" && diagnostic.Message.Contains("was not found"));
        remote.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsyncUnderManualLifecycleStillFailsWithoutAHost()
    {
        var settings = CreateSettings();
        settings.Processing.Mode = ProcessingModes.RemoteSsh;

        var report = await CreateDoctor(new FakeToolVerifier(true)).RunAsync(new LoadedConfiguration(settings, Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.InvalidConfiguration);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "RemoteHostRequired" && diagnostic.Status == DiagnosticStatus.Fail);
    }

    [Fact]
    public async Task RunAsyncWithoutAnImmichBaseUrlNeverCallsImmich()
    {
        var immich = new FakeImmichClientFactory(new ImmichServerVersion(3, 2, 2));

        var report = await CreateDoctor(new FakeToolVerifier(true), immich: immich).RunAsync(new LoadedConfiguration(CreateSettings(), Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.Success);
        immich.Created.Should().BeFalse();
        report.Diagnostics.Should().NotContain(diagnostic => diagnostic.Code.StartsWith("Immich", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsyncReportsTheImmichVersionAndUserAndPreparesTheStagingDirectory()
    {
        var settings = CreateImmichSettings();

        var report = await CreateDoctor(new FakeToolVerifier(true), immich: new FakeImmichClientFactory(new ImmichServerVersion(3, 2, 2))).RunAsync(new LoadedConfiguration(settings, Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.Success);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "ImmichServerAvailable" && diagnostic.Message.Contains("3.2.2"));
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "ImmichApiKeyAccepted" && diagnostic.Message.Contains("Pat"));
        Directory.Exists(settings.Immich.StagingDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsyncFailsForAnImmichServerThatIsNotVersion3WithoutTryingTheKey()
    {
        var immich = new FakeImmichClientFactory(new ImmichServerVersion(2, 5, 6));

        var report = await CreateDoctor(new FakeToolVerifier(true), immich: immich).RunAsync(new LoadedConfiguration(CreateImmichSettings(), Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.MissingDependency);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "ImmichVersionUnsupported" && diagnostic.Message.Contains("2.5.6"));
        immich.UserRequested.Should().BeFalse();
    }

    [Theory]
    [InlineData("ImmichApiKeyRejected")]
    [InlineData("ImmichPermissionMissing")]
    public async Task RunAsyncFailsWithTheImmichClientsDiagnosisOfAKeyFailure(string code)
    {
        var immich = new FakeImmichClientFactory(new ImmichServerVersion(3, 2, 2), new ImmichApiException(code, "what to fix"));

        var report = await CreateDoctor(new FakeToolVerifier(true), immich: immich).RunAsync(new LoadedConfiguration(CreateImmichSettings(), Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.MissingDependency);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == code && diagnostic.Status == DiagnosticStatus.Fail && diagnostic.Message == "what to fix");
    }

    private static DoctorService CreateDoctor(IToolVerifier verifier, IRemoteEnvironmentVerifier? remote = null, IRemoteWorkerLifecycle? lifecycle = null, IImmichClientFactory? immich = null) =>
        new(new SettingsValidator(), new SqliteDatabaseInitializer(), verifier, remote ?? new FakeRemoteEnvironmentVerifier(), lifecycle ?? new FakeRemoteWorkerLifecycle(worker: null), immich ?? new FakeImmichClientFactory(new ImmichServerVersion(3, 2, 2)));

    private AppSettings CreateImmichSettings()
    {
        var settings = CreateSettings();
        settings.Immich = new ImmichSettings { BaseUrl = "http://192.168.1.113:2283", StagingDirectory = Path.Combine(_directory, "immich-staging") };
        return settings;
    }

    private AppSettings CreateHetznerSettings()
    {
        var settings = CreateSettings();
        var identityFile = Path.Combine(_directory, "id_ed25519");
        var bootstrapScript = Path.Combine(_directory, "bootstrap.sh");
        File.WriteAllText(identityFile, "key");
        File.WriteAllText(bootstrapScript, "#!/bin/sh");
        settings.Processing.Mode = ProcessingModes.RemoteSsh;
        settings.Processing.RemoteSsh.Lifecycle = RemoteLifecycleModes.Hetzner;
        settings.Processing.RemoteSsh.IdentityFile = identityFile;
        settings.Processing.RemoteSsh.Hetzner.SshKeyName = "video-optimiser";
        settings.Processing.RemoteSsh.Hetzner.BootstrapScriptPath = bootstrapScript;
        return settings;
    }

    private AppSettings CreateSettings()
    {
        var watchRoot = Path.Combine(_directory, "watch");
        Directory.CreateDirectory(watchRoot);
        return new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = watchRoot }] },
            Eligibility = new EligibilitySettings { Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "8Mbps", MinimumFileSize = "800MiB" }] },
            Original = new OriginalSettings { Action = "delete" }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeToolVerifier(bool isAvailable, string? unavailableName = null) : IToolVerifier
    {
        public List<string> VerifiedNames { get; } = [];

        public Task<ToolVerificationResult> VerifyAsync(string name, string executable, string versionArgument, CancellationToken cancellationToken = default) =>
            VerifyCore(name);

        public Task<ToolVerificationResult> VerifyPresenceAsync(string name, string executable, string availabilityArgument, CancellationToken cancellationToken = default) =>
            VerifyCore(name);

        private Task<ToolVerificationResult> VerifyCore(string name)
        {
            VerifiedNames.Add(name);
            var available = isAvailable && !string.Equals(name, unavailableName, StringComparison.Ordinal);
            return Task.FromResult(new ToolVerificationResult(name, available, available ? "version 1" : "not found"));
        }
    }

    private sealed class FakeRemoteEnvironmentVerifier : IRemoteEnvironmentVerifier
    {
        public bool WasCalled => CheckedHost is not null;
        public string? CheckedHost { get; private set; }

        public Task<IReadOnlyList<Diagnostic>> VerifyAsync(string sshPath, RemoteSshSettings settings, CancellationToken cancellationToken = default)
        {
            CheckedHost = settings.Host;
            IReadOnlyList<Diagnostic> diagnostics =
            [
                new(DiagnosticCategory.Dependency, DiagnosticStatus.Pass, "RemoteSshAvailable", "SSH succeeded.")
            ];
            return Task.FromResult(diagnostics);
        }
    }

    private sealed class FakeImmichClientFactory(ImmichServerVersion version, ImmichApiException? userFailure = null) : IImmichClientFactory, IImmichClient
    {
        public bool Created { get; private set; }
        public bool UserRequested { get; private set; }

        public IImmichClient Create(ImmichSettings settings)
        {
            Created = true;
            return this;
        }

        public Task<ImmichServerVersion> GetServerVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult(version);

        public Task<ImmichUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        {
            UserRequested = true;
            return userFailure is null ? Task.FromResult(new ImmichUser("user-1", "Pat")) : Task.FromException<ImmichUser>(userFailure);
        }
    }

    private sealed class FakeRemoteWorkerLifecycle(RemoteWorkerInfo? worker, Exception? failure = null) : IRemoteWorkerLifecycle
    {
        public bool IsManaged(AppSettings settings) =>
            settings.Processing.Mode == ProcessingModes.RemoteSsh && settings.Processing.RemoteSsh.Lifecycle == RemoteLifecycleModes.Hetzner;

        public Task<RemoteWorkerInfo?> GetAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default)
        {
            if (failure is not null) throw failure;
            // Mirrors the real lifecycle, which points the settings at a live worker.
            if (worker is not null) settings.Processing.RemoteSsh.Host = worker.Address;
            return Task.FromResult(worker);
        }

        public Task<RemoteWorkerInfo> EnsureReadyAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
