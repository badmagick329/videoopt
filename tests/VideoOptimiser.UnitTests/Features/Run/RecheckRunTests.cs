using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Validate;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// What <c>run</c> prints when it checks a video again that failed validation under older rules (VOPT-119), through the real
/// CLI: the lines come between the banner and the summary, with nothing queued.
/// </summary>
public sealed class RecheckRunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    public RecheckRunTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    [Fact]
    public async Task ARunChecksAFailedVideoAgainAndSaysWhatItFound()
    {
        var config = await RunCli.WriteConfigAsync(DatabasePath);
        var original = Path.Combine(_directory, "clip.mp4");
        var av1 = Path.Combine(_directory, "clip.av1.mp4");
        await File.WriteAllTextAsync(original, "original");
        await File.WriteAllTextAsync(av1, "av1");
        await new OutputManifestStore().SaveAsync(new OutputManifest
        {
            SourcePath = original,
            SourceFingerprint = await new FileFingerprintService().CreateAsync(original),
            OutputPath = av1,
            Validation = new ValidationReport { Passed = false, Failures = ["old rule"] }
        });
        var job = await _repository.CreateAsync(DatabasePath, new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = original,
            OutputPath = av1,
            Status = JobStatus.Failed,
            FailureCategory = FailureCategories.ValidationFailed,
            FailureMessage = "old rule"
        });

        var (code, output, error) = await ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            services.AddSingleton<IOutputValidationService>(new WarningValidator());
        }, "run", "--local", "--config", config);

        error.Should().BeEmpty();
        code.Should().Be((int)ExitCode.Success);
        output.Should().Be(RunCli.Transcript(
            "Encoding on this PC (--local).",
            "Checking the AV1 of 1 video that failed validation earlier.",
            "",
            "[1/1] clip.mp4 (8 B)",
            "      Checking the output: streams, duration, savings and decode test.",
            "      Ready to finalise: 100 B -> 50 B, 50.0% saved. Check it before finalising:",
            "        audio lasts 2 s longer",
            "",
            "Checked again: 1 ready to finalise (1 to check).",
            $"1 video is ready to finalise: review it, then run 'video-optimiser finalize --ready --config \"{config}\"'."));
        (await _repository.GetAsync(DatabasePath, job.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class WarningValidator : IOutputValidationService
    {
        public Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationReport { Passed = true, Warnings = ["audio lasts 2 s longer"], SourceSizeBytes = 100, OutputSizeBytes = 50, PercentageSaved = 50 });
    }
}
