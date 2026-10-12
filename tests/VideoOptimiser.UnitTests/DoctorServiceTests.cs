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
    public async Task RunAsyncReportsWhereEncodingRunsAsTheFirstProcessingDiagnosticBeforeAnyDependency()
    {
        var settings = CreateHetznerSettings();
        var worker = new RemoteWorkerInfo(42, "video-optimiser-1", "203.0.113.7", "running", new DateTimeOffset(2026, 10, 12, 18, 4, 0, TimeSpan.Zero), "cx43", "hel1");

        var report = await CreateDoctor(new FakeToolVerifier(true), new FakeRemoteEnvironmentVerifier(), new FakeRemoteWorkerLifecycle(worker)).RunAsync(new LoadedConfiguration(settings, Path.Combine(_directory, "config.yaml")));

        var location = report.Diagnostics.First(diagnostic => diagnostic.Category == DiagnosticCategory.Processing);
        location.Code.Should().Be("EncodingLocation");
        location.Status.Should().Be(DiagnosticStatus.Pass);
        location.Message.Should().Be(RunBanner.Describe(settings, worker));
        report.Diagnostics.TakeWhile(diagnostic => diagnostic != location).Should().NotContain(diagnostic => diagnostic.Category == DiagnosticCategory.Dependency);
    }

    [Fact]
    public async Task RunAsyncStillDescribesTheEncodingLocationWhenTheWorkerStateCannotBeRead()
    {
        var settings = CreateHetznerSettings();
        var lifecycle = new FakeRemoteWorkerLifecycle(worker: null, failure: new InvalidDataException("bad state"));

        var report = await CreateDoctor(new FakeToolVerifier(true), new FakeRemoteEnvironmentVerifier(), lifecycle).RunAsync(new LoadedConfiguration(settings, Path.Combine(_directory, "config.yaml")));

        report.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "EncodingLocation")
            .Which.Message.Should().StartWith("Encoding on: a Hetzner server created for this run");
    }

    [Fact]
    public async Task RunAsyncUnderHetznerChecksTheRunningWorker()
    {
        var settings = CreateHetznerSettings();
        var remote = new FakeRemoteEnvironmentVerifier();
        var worker = new RemoteWorkerInfo(42, "video-optimiser-1", "203.0.113.7", "running", null, "cx43", "hel1");

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

    [Theory]
    [InlineData("ab-av1 0.9.3")]
    [InlineData("ab-av1 0.11.6")]
    public async Task RunAsyncInLocalModeFailsAnAbAv1OlderThanTheMinimum(string versionLine)
    {
        var report = await CreateDoctor(new FakeToolVerifier(true) { AbAv1VersionLine = versionLine }).RunAsync(new LoadedConfiguration(CreateSettings(), Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.MissingDependency);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "AbAv1VersionInsufficient" && diagnostic.Status == DiagnosticStatus.Fail
            && diagnostic.Message.Contains("is too old") && diagnostic.Message.Contains("cargo install ab-av1 --version 0.11.7 --locked"));
    }

    [Theory]
    [InlineData("ab-av1 0.11.7")]
    [InlineData("ab-av1 0.12.0")]
    public async Task RunAsyncInLocalModePassesAnAbAv1AtOrAboveTheMinimum(string versionLine)
    {
        var report = await CreateDoctor(new FakeToolVerifier(true) { AbAv1VersionLine = versionLine }).RunAsync(new LoadedConfiguration(CreateSettings(), Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.Success);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "AbAv1VersionSufficient" && diagnostic.Status == DiagnosticStatus.Pass);
    }

    [Fact]
    public async Task RunAsyncInLocalModeFailsWhenTheAbAv1VersionCannotBeParsed()
    {
        var report = await CreateDoctor(new FakeToolVerifier(true) { AbAv1VersionLine = "something unexpected" }).RunAsync(new LoadedConfiguration(CreateSettings(), Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.MissingDependency);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "AbAv1VersionUnavailable" && diagnostic.Status == DiagnosticStatus.Fail);
    }

    [Fact]
    public async Task RunAsyncInLocalModeFailsWhenFfmpegLacksLibvmaf()
    {
        var tools = new FakeToolVerifier(true) { Filters = " ... vmafmotion       V->V       Calculate the VMAF Motion score." };

        var report = await CreateDoctor(tools).RunAsync(new LoadedConfiguration(CreateSettings(), Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.MissingDependency);
        report.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "FfmpegFeatureUnavailable" && diagnostic.Message.Contains("libvmaf"));
        report.Diagnostics.Count(diagnostic => diagnostic.Code == "FfmpegFeatureAvailable").Should().Be(2);
    }

    [Fact]
    public async Task RunAsyncInLocalModeDoesNotAcceptAFeatureNameThatIsOnlyAPrefixOfAnotherToken()
    {
        var tools = new FakeToolVerifier(true) { Encoders = " V....D libsvtav1x           hypothetical encoder" };

        var report = await CreateDoctor(tools).RunAsync(new LoadedConfiguration(CreateSettings(), Path.Combine(_directory, "config.yaml")));

        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "FfmpegFeatureUnavailable" && diagnostic.Message.Contains("libsvtav1"));
    }

    [Fact]
    public async Task RunAsyncInLocalModeFailsTheFeatureChecksWhenFfmpegListingsCannotBeRead()
    {
        var report = await CreateDoctor(new FakeToolVerifier(true) { ListingsReadable = false }).RunAsync(new LoadedConfiguration(CreateSettings(), Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.MissingDependency);
        report.Diagnostics.Count(diagnostic => diagnostic.Code == "FfmpegFeatureUnavailable" && diagnostic.Message.Contains("could not be read")).Should().Be(3);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "Av1DecoderUnavailable" && diagnostic.Message.Contains("could not be read"));
    }

    [Fact]
    public async Task RunAsyncInLocalModeFailsWithoutTheLibdav1dDecoder()
    {
        var tools = new FakeToolVerifier(true) { Decoders = " V....D av1                  Alliance for Open Media AV1" };

        var report = await CreateDoctor(tools).RunAsync(new LoadedConfiguration(CreateSettings(), Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.MissingDependency);
        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "Av1DecoderUnavailable" && diagnostic.Message.Contains("gyan.dev"));
    }

    [Fact]
    public async Task RunAsyncSkipsTheLibdav1dCheckWhenTheDecodeTestIsOff()
    {
        var settings = CreateSettings();
        settings.Validation.DecodeTest = DecodeTestModes.None;
        var tools = new FakeToolVerifier(true) { Decoders = " V....D av1                  Alliance for Open Media AV1" };

        var report = await CreateDoctor(tools).RunAsync(new LoadedConfiguration(settings, Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.Success);
        report.Diagnostics.Should().NotContain(diagnostic => diagnostic.Code.StartsWith("Av1Decoder", StringComparison.Ordinal));
        tools.ListingsRequested.Should().NotContain("-decoders");
    }

    [Fact]
    public async Task RunAsyncInRemoteModeRequiresLibdav1dButNotTheLocalEncodeFeatures()
    {
        var settings = CreateSettings();
        settings.Processing.Mode = ProcessingModes.RemoteSsh;
        settings.Processing.RemoteSsh.Host = "video-worker";
        var tools = new FakeToolVerifier(true)
        {
            AbAv1VersionLine = "ab-av1 0.9.3",
            Encoders = string.Empty,
            Filters = string.Empty,
            PixelFormats = string.Empty,
            Decoders = " V....D av1                  Alliance for Open Media AV1"
        };

        var report = await CreateDoctor(tools).RunAsync(new LoadedConfiguration(settings, Path.Combine(_directory, "config.yaml")));

        report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "Av1DecoderUnavailable");
        report.Diagnostics.Should().NotContain(diagnostic => diagnostic.Code.StartsWith("FfmpegFeature", StringComparison.Ordinal) || diagnostic.Code.StartsWith("AbAv1Version", StringComparison.Ordinal));
        tools.ListingsRequested.Should().BeEquivalentTo(["-decoders"]);
    }

    [Fact]
    public async Task RunAsyncDoesNotQueryFfmpegWhenItIsUnavailable()
    {
        var tools = new FakeToolVerifier(true, unavailableName: "ffmpeg");

        var report = await CreateDoctor(tools).RunAsync(new LoadedConfiguration(CreateSettings(), Path.Combine(_directory, "config.yaml")));

        report.ExitCode.Should().Be(ExitCode.MissingDependency);
        tools.ListingsRequested.Should().BeEmpty();
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

    // Defaults describe a healthy install: ab-av1 at the minimum version and an FFmpeg exposing every required feature.
    private sealed class FakeToolVerifier(bool isAvailable, string? unavailableName = null) : IToolVerifier
    {
        public List<string> VerifiedNames { get; } = [];
        public string AbAv1VersionLine { get; init; } = "ab-av1 0.11.7";
        public string Encoders { get; init; } = " V....D libsvtav1           SVT-AV1(Scalable Video Technology for AV1) encoder (codec av1)";
        public string Filters { get; init; } = " ... libvmaf          VV->V      Calculate the VMAF between two video streams.";
        public string PixelFormats { get; init; } = "IO... yuv420p10le          3            10";
        public string Decoders { get; init; } = " V....D libdav1d           dav1d AV1 decoder by VideoLAN (codec av1)";
        // Models ffmpeg failing to run a listing query.
        public bool ListingsReadable { get; init; } = true;
        public List<string> ListingsRequested { get; } = [];

        public Task<ToolVerificationResult> VerifyAsync(string name, string executable, string versionArgument, CancellationToken cancellationToken = default) =>
            VerifyCore(name);

        public Task<ToolVerificationResult> VerifyPresenceAsync(string name, string executable, string availabilityArgument, CancellationToken cancellationToken = default) =>
            VerifyCore(name);

        public Task<string?> ReadOutputAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            var listing = arguments[^1];
            lock (ListingsRequested) ListingsRequested.Add(listing);
            if (!ListingsReadable) return Task.FromResult<string?>(null);
            return Task.FromResult<string?>(listing switch
            {
                "-encoders" => Encoders,
                "-filters" => Filters,
                "-pix_fmts" => PixelFormats,
                "-decoders" => Decoders,
                _ => throw new NotSupportedException(listing)
            });
        }

        private Task<ToolVerificationResult> VerifyCore(string name)
        {
            VerifiedNames.Add(name);
            var available = isAvailable && !string.Equals(name, unavailableName, StringComparison.Ordinal);
            var detail = !available ? "not found" : name == "ab-av1" ? AbAv1VersionLine : "version 1";
            return Task.FromResult(new ToolVerificationResult(name, available, detail));
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

        // doctor only checks the version and the key.
        public IAsyncEnumerable<ImmichAsset> SearchVideosAsync(DateTimeOffset takenBefore, bool includeFavorites, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadOriginalAsync(string assetId, string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichUploadResult> UploadAsync(ImmichUpload upload, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateMetadataAsync(string assetId, ImmichMetadataUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyRelationsAsync(string sourceAssetId, string targetAssetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TagAssetAsync(string assetId, IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TrashAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ImmichUploadCheck>> CheckUploadsAsync(IReadOnlyList<string> checksums, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

        public Task<RemoteWorkerInfo?> ReadRecordedAsync(string databasePath, CancellationToken cancellationToken = default) =>
            failure is not null ? throw failure : Task.FromResult(worker);

        public Task<RemoteWorkerInfo> EnsureReadyAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
