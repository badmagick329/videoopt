using FluentAssertions;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Processing;
using VideoOptimiser.Infrastructure.Scanning;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// A fake ffprobe stands in for the real one: it prints a valid H.264 probe for any path, and fails with
/// ffprobe's own "moov atom not found" for a path containing "truncated". Windows-only, like the tool itself.
/// </summary>
public sealed class ProbeFailureReportingTests : IDisposable
{
    private const string MoovAtomMessage = "[mov,mp4,m4a,3gp,3g2,mj2 @ 000001] moov atom not found";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly string _ffprobe;

    public ProbeFailureReportingTests()
    {
        Directory.CreateDirectory(_directory);
        _ffprobe = Path.Combine(_directory, "fake-ffprobe.cmd");
        File.WriteAllText(_ffprobe, string.Join("\r\n",
            "@echo off",
            "echo %* | findstr /i \"truncated\" >nul",
            "if not errorlevel 1 (",
            $"  echo {MoovAtomMessage} 1>&2",
            "  exit /b 1",
            ")",
            "echo {\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\"}]}",
            ""));
    }

    [Fact]
    public async Task FfprobeMediaProbeReportsANonZeroExitAsAProbeFailureNotInvalidData()
    {
        var action = () => new FfprobeMediaProbe(_ffprobe).ProbeAsync(Path.Combine(_directory, "truncated.mp4"));

        (await action.Should().ThrowAsync<MediaProbeException>()).WithMessage($"ffprobe exited with 1: {MoovAtomMessage}");
    }

    [Fact]
    public async Task FfprobeMediaTimelineProbeReportsANonZeroExitAsAProbeFailureNotInvalidData()
    {
        var action = () => new FfprobeMediaTimelineProbe(_ffprobe).ProbeAsync(Path.Combine(_directory, "truncated.mp4"));

        (await action.Should().ThrowAsync<MediaProbeException>()).WithMessage($"ffprobe exited with 1: {MoovAtomMessage}");
    }

    [Fact]
    public async Task ValidateCommandFailsAnUnreadableOutputWithExitSevenAndRecordsTheFailure()
    {
        var (jobId, config, repository, databasePath) = await ArrangeJobAsync(source: "movie.mp4", output: "movie.truncated.mp4");

        var (exitCode, stderr) = await RunCliAsync("validate", jobId.ToString("N"), "--config", config);

        exitCode.Should().Be((int)ExitCode.ValidationFailure);
        stderr.Should().NotContain("Configuration could not be read");
        var job = await repository.GetAsync(databasePath, jobId);
        job!.Status.Should().Be(JobStatus.Failed);
        job.FailureCategory.Should().Be("ValidationFailed");
        job.FailureMessage.Should().Contain("movie.truncated.mp4").And.Contain("moov atom not found");
        var manifest = await new OutputManifestStore().LoadAsync(job.OutputPath!);
        manifest.Validation!.Passed.Should().BeFalse();
        manifest.Validation.Failures.Should().ContainSingle().Which.Should().Contain("moov atom not found");
    }

    [Fact]
    public async Task GlobalHandlerReportsAnUnreadableOriginalAsAProcessingFailureNotAConfigurationError()
    {
        var (jobId, config, _, _) = await ArrangeJobAsync(source: "movie.truncated.mp4", output: "movie.mp4");

        var (exitCode, stderr) = await RunCliAsync("validate", jobId.ToString("N"), "--config", config);

        exitCode.Should().Be((int)ExitCode.ProcessingFailure);
        stderr.Should().NotContain("Configuration could not be read").And.Contain("movie.truncated.mp4").And.Contain("moov atom not found");
    }

    [Fact]
    public async Task GlobalHandlerStillReportsAnUnparsableConfigurationAsExitThree()
    {
        var config = Path.Combine(_directory, "bad.yaml");
        await File.WriteAllTextAsync(config, "tools:\n  ffprobePath: [unterminated\n");

        var (exitCode, stderr) = await RunCliAsync("validate", Guid.NewGuid().ToString("N"), "--config", config);

        exitCode.Should().Be((int)ExitCode.InvalidConfiguration);
        stderr.Should().Contain("Configuration could not be read");
    }

    private async Task<(Guid JobId, string Config, SqliteJobRepository Repository, string DatabasePath)> ArrangeJobAsync(string source, string output)
    {
        var sourcePath = Path.Combine(_directory, source);
        var outputPath = Path.Combine(_directory, output);
        await File.WriteAllBytesAsync(sourcePath, new byte[100]);
        await File.WriteAllBytesAsync(outputPath, new byte[50]);
        var databasePath = Path.Combine(_directory, "jobs.db");
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, $"tools:\n  ffprobePath: '{_ffprobe}'\ndatabase:\n  path: '{databasePath}'\n");

        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var job = await repository.CreateAsync(databasePath, new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = sourcePath,
            SourceFingerprint = "fingerprint",
            Status = JobStatus.Validating,
            OutputPath = outputPath
        });
        await new OutputManifestStore().SaveAsync(new OutputManifest { SourcePath = sourcePath, OutputPath = outputPath });
        return (job.Id, config, repository, databasePath);
    }

    // Console streams are process-wide, so this briefly captures them for the whole test run. Nothing else asserts on them.
    private static async Task<(int ExitCode, string Stderr)> RunCliAsync(params string[] args)
    {
        var originalError = Console.Error;
        var originalOut = Console.Out;
        using var error = new StringWriter();
        Console.SetError(error);
        Console.SetOut(TextWriter.Null);
        try
        {
            return (await CliApplication.RunAsync(args), error.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetOut(originalOut);
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
