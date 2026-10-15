using FluentAssertions;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests.Features.Queue;

/// <summary>
/// A fake ffprobe stands in for the real one: it prints a valid H.264 probe for any path, and fails with
/// ffprobe's own "moov atom not found" for a path containing "truncated". Windows-only, like the tool itself.
/// </summary>
[Collection(ConsoleCapture.Collection)]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FfprobeProbesReportANonZeroExitAsAProbeFailureNotInvalidData(bool timelineProbe)
    {
        var path = Path.Combine(_directory, "truncated.mp4");
        Func<Task> action = timelineProbe
            ? () => new FfprobeMediaTimelineProbe(_ffprobe).ProbeAsync(path)
            : () => new FfprobeMediaProbe(_ffprobe).ProbeAsync(path);

        (await action.Should().ThrowAsync<MediaProbeException>()).WithMessage($"ffprobe exited with 1: {MoovAtomMessage}");
    }

    [Fact]
    public async Task ValidateCommandFailsAnUnreadableOutputWithExitSevenAndRecordsTheFailure()
    {
        var (jobId, config, repository, databasePath) = await ArrangeJobAsync(source: "movie.mp4", output: "movie.truncated.mp4");

        var (exitCode, _, stderr) = await ConsoleCapture.RunCliAsync("validate", jobId.ToString("N"), "--config", config);

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

        var (exitCode, _, stderr) = await ConsoleCapture.RunCliAsync("validate", jobId.ToString("N"), "--config", config);

        exitCode.Should().Be((int)ExitCode.ProcessingFailure);
        stderr.Should().NotContain("Configuration could not be read").And.Contain("movie.truncated.mp4").And.Contain("moov atom not found");
    }

    [Fact]
    public async Task GlobalHandlerStillReportsAnUnparsableConfigurationAsExitThree()
    {
        var config = Path.Combine(_directory, "bad.yaml");
        await File.WriteAllTextAsync(config, "tools:\n  ffprobePath: [unterminated\n");

        var (exitCode, _, stderr) = await ConsoleCapture.RunCliAsync("validate", Guid.NewGuid().ToString("N"), "--config", config);

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

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
