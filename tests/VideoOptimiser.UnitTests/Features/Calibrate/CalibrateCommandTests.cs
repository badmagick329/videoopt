using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.Calibrate;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests.Features.Calibrate;

/// <summary>What <c>calibrate</c> prints and returns, for people and for <c>--json</c>; agents parse the JSON, so its shape must hold.</summary>
[Collection(ConsoleCapture.Collection)]
public sealed class CalibrateCommandTests : IDisposable
{
    private const long GiB = 1L << 30;

    // The JSON writer escapes "+" in strings.
    private const string Plus = "\\" + "u002B";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public CalibrateCommandTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task InvalidSettingsAreReportedOnStderrAndTheServiceNeverRuns()
    {
        var service = new FakeCalibration(Run(scanIssues: 0));

        var (code, stdout, stderr) = await RunAsync(service, CliFakes.Diagnostics(
            new Diagnostic(DiagnosticCategory.FileSystem, DiagnosticStatus.Fail, "WatchRootMissing", "Watch root clip-a is missing"),
            new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Fail, "ToolMissing", "ffprobe was not found")), "calibrate");

        code.Should().Be((int)ExitCode.InvalidConfiguration);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be("[WatchRootMissing] Watch root clip-a is missing\n[ToolMissing] ffprobe was not found\n");
        service.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AnOutdatedDatabaseAsksForTheUpgradeAndFails()
    {
        var service = new FakeCalibration(new DatabaseOutdatedException(Path.Combine(_directory, "jobs.db")));

        var (code, stdout, stderr) = await RunAsync(service, CliFakes.AcceptAnySettings, "calibrate");

        code.Should().Be((int)ExitCode.GeneralFailure);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be("jobs.db is from an older version of video-optimiser. Run 'video-optimiser status' once to upgrade it, then run calibrate again.\n");
    }

    [Fact]
    public async Task TheHumanReportIsPrintedAndACleanScanExitsZero()
    {
        var service = new FakeCalibration(Run(scanIssues: 0));

        var (code, stdout, stderr) = await RunAsync(service, CliFakes.AcceptAnySettings, "calibrate");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            "Admitted by your rules (quality target VMAF 97)\n" +
            "  Rule 1  h264 1080p-1440p >=15Mbps >=1MiB  1 video  2.0 GiB  never tried\n" +
            "Kept out by your rules\n" +
            "  hevc 4k+ 20-30 Mbps  1 video  3.0 GiB  never tried\n" +
            "\n" +
            "Suggestions\n" +
            $"  - Never tried: hevc 4k+ 20-30 Mbps (1 video, 3.0 GiB). Test one: video-optimiser queue add \"{Clip}\" --force\n");
        service.Calls.Should().Be(1);
    }

    [Fact]
    public async Task JsonCarriesTheReportWithATypedSuggestionAndScanIssuesEndInPartialSuccess()
    {
        var service = new FakeCalibration(Run(scanIssues: 2));

        var (code, stdout, stderr) = await RunAsync(service, CliFakes.AcceptAnySettings, "calibrate", "--json");

        code.Should().Be((int)ExitCode.PartialSuccess);
        stderr.Should().BeEmpty();
        var command = JsonEncodedText.Encode($"video-optimiser queue add \"{Clip}\" --force", JavaScriptEncoder.Default);
        var path = JsonEncodedText.Encode(Clip, JavaScriptEncoder.Default);
        stdout.ReplaceLineEndings("\n").Should().Be($$"""
            {
              "minimumVmaf": 97,
              "rules": [
                {
                  "number": 1,
                  "codecs": [
                    "h264"
                  ],
                  "resolution": "1080p-1440p",
                  "minimumVideoBitrate": "15Mbps",
                  "minimumFileSize": "1MiB",
                  "videos": 1,
                  "bytes": 2147483648,
                  "history": null
                }
              ],
              "keptOut": [
                {
                  "codec": "hevc",
                  "resolution": "4k{{Plus}}",
                  "minimumMbps": 20,
                  "maximumMbps": 30,
                  "videos": 1,
                  "bytes": 3221225472,
                  "history": null
                }
              ],
              "codecUnknown": [],
              "alreadyAv1": {
                "videos": 0,
                "bytes": 0
              },
              "historyLeftOut": {
                "otherSettings": 0,
                "noVideoFacts": 0
              },
              "suggestions": [
                {
                  "kind": "testOne",
                  "codec": "hevc",
                  "resolution": "4k{{Plus}}",
                  "minimumMbps": 20,
                  "maximumMbps": 30,
                  "path": "{{path}}",
                  "immichAssetId": null,
                  "command": "{{command}}",
                  "text": "Never tried: hevc 4k{{Plus}} 20-30 Mbps (1 video, 3.0 GiB). Test one: {{command}}"
                }
              ]
            }

            """);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Clip => @"C:\videos\clip-b.mp4";

    // One video a rule admits and one it keeps out, so the report holds a rule row, a group and a "test one" suggestion.
    private static CalibrationRun Run(int scanIssues)
    {
        var settings = new AppSettings
        {
            Eligibility = new EligibilitySettings { Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "15Mbps", MinimumFileSize = "1MiB" }] },
            Quality = new QualitySettings { MinimumVmaf = 97 }
        };
        CalibrationVideo[] videos =
        [
            new("clip-a.mp4", @"C:\videos\clip-a.mp4", null, "h264", 1920, 1080, 20_000_000, 2 * GiB),
            new("clip-b.mp4", Clip, null, "hevc", 3840, 2160, 25_000_000, 3 * GiB)
        ];
        return new(CalibrationReport.Build(settings, videos, [], "current-settings"), scanIssues);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(FakeCalibration service, Action<IServiceCollection> settings, params string[] args)
    {
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, $"database:\n  path: '{Path.Combine(_directory, "jobs.db")}'\n");
        return await ConsoleCapture.RunCliAsync(services =>
        {
            settings(services);
            services.AddSingleton<ICalibrationService>(service);
        }, [.. args, "--config", config]);
    }

    private sealed class FakeCalibration : ICalibrationService
    {
        private readonly CalibrationRun? _run;
        private readonly Exception? _failure;

        public FakeCalibration(CalibrationRun run) => _run = run;

        public FakeCalibration(Exception failure) => _failure = failure;

        public int Calls { get; private set; }

        public Task<CalibrationRun> RunAsync(AppSettings settings, IProgress<ScanProgress>? scanProgress, IProgress<int>? immichProgress, CancellationToken cancellationToken = default)
        {
            Calls++;
            // As the real scan does, so a progress console wrongly given in --json mode would print before the JSON.
            if (_run?.ScanIssues > 0) scanProgress?.Report(new ScanProgress(string.Empty, "Issue", "Cannot read the watch root"));
            return _failure is null ? Task.FromResult(_run!) : Task.FromException<CalibrationRun>(_failure);
        }
    }
}
