using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Doctor;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests.Features.Doctor;

/// <summary>What <c>doctor</c> prints, for people and for <c>--json</c>; agents parse the JSON, so its shape must hold.</summary>
[Collection(ConsoleCapture.Collection)]
public sealed class DoctorCommandTests : IDisposable
{
    private static readonly DoctorReport Report = new([
        new Diagnostic(DiagnosticCategory.Configuration, DiagnosticStatus.Pass, "ConfigurationValid", "Configuration is valid"),
        new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Fail, "ToolMissing", "ffprobe was not found")]);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public DoctorCommandTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task PrintsOneLinePerDiagnosticAndExitsWithTheReportsCode()
    {
        var (code, stdout, _) = await ConsoleCapture.RunCliAsync(UseReport, "doctor", "--config", await WriteConfigAsync());

        code.Should().Be((int)ExitCode.MissingDependency);
        stdout.ReplaceLineEndings("\n").Should().Be(
            "Pass    Configuration Configuration is valid\n" +
            "Fail    Dependency    ffprobe was not found\n");
    }

    [Fact]
    public async Task JsonCarriesThePathTheExitCodeAndEachDiagnostic()
    {
        var config = await WriteConfigAsync();

        var (code, stdout, _) = await ConsoleCapture.RunCliAsync(UseReport, "doctor", "--json", "--config", config);

        code.Should().Be((int)ExitCode.MissingDependency);
        var path = JsonEncodedText.Encode(Path.GetFullPath(config), JavaScriptEncoder.Default);
        stdout.ReplaceLineEndings("\n").Should().Be($$"""
            {
              "configurationPath": "{{path}}",
              "exitCode": 4,
              "diagnostics": [
                {
                  "category": "Configuration",
                  "status": "Pass",
                  "code": "ConfigurationValid",
                  "message": "Configuration is valid"
                },
                {
                  "category": "Dependency",
                  "status": "Fail",
                  "code": "ToolMissing",
                  "message": "ffprobe was not found"
                }
              ]
            }

            """);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static void UseReport(IServiceCollection services) => services.AddSingleton<IDoctorService>(new FixedDoctor());

    private async Task<string> WriteConfigAsync()
    {
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, $"database:\n  path: '{Path.Combine(_directory, "jobs.db")}'\n");
        return config;
    }

    private sealed class FixedDoctor : IDoctorService
    {
        public Task<DoctorReport> RunAsync(LoadedConfiguration configuration, CancellationToken cancellationToken = default) => Task.FromResult(Report);
    }
}
