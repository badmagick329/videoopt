using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Diagnostics;
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

    private static DoctorService CreateDoctor(IToolVerifier verifier, IRemoteEnvironmentVerifier? remote = null) =>
        new(new SettingsValidator(), new SqliteDatabaseInitializer(), verifier, remote ?? new FakeRemoteEnvironmentVerifier());

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
        public bool WasCalled { get; private set; }

        public Task<IReadOnlyList<Diagnostic>> VerifyAsync(string sshPath, RemoteSshSettings settings, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            IReadOnlyList<Diagnostic> diagnostics =
            [
                new(DiagnosticCategory.Dependency, DiagnosticStatus.Pass, "RemoteSshAvailable", "SSH succeeded.")
            ];
            return Task.FromResult(diagnostics);
        }
    }
}
