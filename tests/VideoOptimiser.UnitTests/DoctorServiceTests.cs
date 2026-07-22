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

    private static DoctorService CreateDoctor(IToolVerifier verifier) => new(new SettingsValidator(), new SqliteDatabaseInitializer(), verifier);

    private AppSettings CreateSettings()
    {
        var watchRoot = Path.Combine(_directory, "watch");
        Directory.CreateDirectory(watchRoot);
        return new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Logging = new LoggingSettings { Directory = Path.Combine(_directory, "logs") },
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

    private sealed class FakeToolVerifier(bool isAvailable) : IToolVerifier
    {
        public Task<ToolVerificationResult> VerifyAsync(string name, string executable, string versionArgument, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolVerificationResult(name, isAvailable, isAvailable ? "version 1" : "not found"));
    }
}
