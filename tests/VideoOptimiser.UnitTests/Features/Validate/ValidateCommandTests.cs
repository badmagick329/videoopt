using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Validate;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;

namespace VideoOptimiser.UnitTests.Features.Validate;

/// <summary>
/// F7 (VOPT-60): validate re-checks only a job on its way to finalising. A failed validation fails the job, and cleanup then
/// deletes its AV1, so a job whose files are missing, or that has already ended, is refused and left as it is. Validation is
/// faked throughout, because what these pin is what validate prints, returns and records around it. Each refusal also asserts
/// the fake was never called, so a refused job is never judged. The checks themselves are <c>OutputValidationServiceTests</c>.
/// </summary>
public sealed class ValidateCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeValidation _validation = new();

    public ValidateCommandTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    /// <summary>Where <c>RunCli.WriteConfigAsync</c> puts the config, which the hints in the refusals repeat.</summary>
    private string ConfigPath => Path.Combine(_directory, "config.yaml");

    [Fact]
    public async Task AFinishedJobIsRefusedAndItsValidationRecordLeftAlone()
    {
        var job = await CreateReadyJobAsync(after: [JobStatus.Finalizing, JobStatus.Completed]);
        var manifestPath = new OutputManifestStore().GetPath(job.OutputPath!);
        var before = await File.ReadAllTextAsync(manifestPath);

        var (code, _, stderr) = await RunAsync(job.Id.ToString("N"));

        code.Should().Be((int)ExitCode.ProcessingFailure);
        stderr.Trim().Should().Be($"validate re-checks a job that is ready to finalise; {job.Id:N} is Completed.");
        (await File.ReadAllTextAsync(manifestPath)).Should().Be(before);
        _validation.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AMissingOriginalIsReportedAndTheJobLeftAsItIs()
    {
        var job = await CreateReadyJobAsync();
        File.Delete(job.SourcePath);

        var (code, _, stderr) = await RunAsync(job.Id.ToString("N"));

        code.Should().Be((int)ExitCode.ProcessingFailure);
        stderr.Trim().Should().Be($"Original does not exist: {job.SourcePath}.");
        (await _repository.GetAsync(DatabasePath, job.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
        _validation.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AnInvalidJobIdIsRefused()
    {
        var (code, stdout, stderr) = await RunAsync("not-an-id");

        code.Should().Be((int)ExitCode.InvalidArguments);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be("Job ID is invalid.\n");
        _validation.Calls.Should().Be(0);
    }

    /// <summary>Two branches with one message: no such job, and a job with no output path to check.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AJobWithNothingToCheckIsNotFound(bool inTheDatabase)
    {
        var jobId = inTheDatabase ? (await CreateReadyJobAsync(withOutput: false)).Id : Guid.NewGuid();

        var (code, stdout, stderr) = await RunAsync(jobId.ToString("N"));

        code.Should().Be((int)ExitCode.ProcessingFailure);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be("Job or temporary output was not found.\n");
        _validation.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AMissingAv1IsReportedAndTheJobLeftAsItIs()
    {
        var job = await CreateReadyJobAsync();
        File.Delete(job.OutputPath!);

        var (code, stdout, stderr) = await RunAsync(job.Id.ToString("N"));

        code.Should().Be((int)ExitCode.ProcessingFailure);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be($"The AV1 is missing: {job.OutputPath}.\n");
        (await _repository.GetAsync(DatabasePath, job.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
        _validation.Calls.Should().Be(0);
    }

    /// <summary>
    /// VOPT-88: a missing, unreadable (a folder where the file should be) or corrupt record is refused like the other missing
    /// pieces. Validate must not rebuild it from the job, which could hide an original that changed after encoding.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("unreadable")]
    public async Task ARecordThatCannotBeLoadedIsRefusedAndTheJobAndRecordLeftAsTheyAre(string damage)
    {
        var job = await CreateReadyJobAsync();
        var manifestPath = new OutputManifestStore().GetPath(job.OutputPath!);
        File.Delete(manifestPath);
        if (damage == "corrupt") await File.WriteAllTextAsync(manifestPath, "{ not json");
        if (damage == "unreadable") Directory.CreateDirectory(manifestPath);

        var (code, stdout, stderr) = await RunAsync(job.Id.ToString("N"));

        code.Should().Be((int)ExitCode.ProcessingFailure);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be($"The AV1's validation record is missing or unreadable: {manifestPath}, so the AV1 can't be checked. To convert the video again, run 'video-optimiser queue drop {job.Id:N} --config \"{ConfigPath}\"', then 'video-optimiser queue retry {job.Id:N} --config \"{ConfigPath}\"'.\n");
        if (damage == "corrupt") (await File.ReadAllTextAsync(manifestPath)).Should().Be("{ not json");
        if (damage == "missing") File.Exists(manifestPath).Should().BeFalse();
        var stored = (await _repository.GetAsync(DatabasePath, job.Id))!;
        (stored.Status, stored.ValidationPassed).Should().Be((JobStatus.ReadyToFinalize, job.ValidationPassed));
        _validation.Calls.Should().Be(0);
    }

    /// <summary>
    /// VOPT-91: a held job stopped while its AV1 was checked passes the status check. Validating it would save the record
    /// and then fail the write to the held row, every time, so the refusal comes first and nothing is written.
    /// </summary>
    [Fact]
    public async Task AHeldJobIsRefusedBeforeAnythingIsValidatedOrWritten()
    {
        var job = await CreateReadyJobAsync(after: [JobStatus.Validating]);
        job.ResumeStatus = JobStatus.Validating;
        job.Status = JobStatus.Interrupted;
        await _repository.UpdateAsync(DatabasePath, job);
        (await _repository.SetHeldAsync(DatabasePath, job, held: true)).Should().BeTrue();
        var manifestPath = new OutputManifestStore().GetPath(job.OutputPath!);
        var before = await File.ReadAllTextAsync(manifestPath);

        var (code, stdout, stderr) = await RunAsync(job.Id.ToString("N"));

        code.Should().Be((int)ExitCode.ProcessingFailure);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be($"clip-a.mp4 is held. To check its AV1, run 'video-optimiser queue release {job.Id:N} --config \"{ConfigPath}\"' first.\n");
        (await File.ReadAllTextAsync(manifestPath)).Should().Be(before);
        var stored = (await _repository.GetAsync(DatabasePath, job.Id))!;
        (stored.Status, stored.ResumeStatus, stored.Held).Should().Be((JobStatus.Interrupted, JobStatus.Validating, true));
        _validation.Calls.Should().Be(0);
    }

    [Fact]
    public async Task APassRecordsTheSavingsClearsAnOldFailureAndReadiesTheJob()
    {
        var job = await CreateReadyJobAsync();
        job.FailureCategory = FailureCategories.FinalizationIncomplete;
        job.FailureMessage = "The upload finished.";
        await _repository.UpdateAsync(DatabasePath, job);
        _validation.Report = new ValidationReport { Passed = true, SourceSizeBytes = 1000, OutputSizeBytes = 400, PercentageSaved = 60m };

        var (code, stdout, stderr) = await RunAsync(job.Id.ToString("N"));

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be("Validation passed: saved 60.0%.\n");
        var stored = (await _repository.GetAsync(DatabasePath, job.Id))!;
        stored.Status.Should().Be(JobStatus.ReadyToFinalize);
        (stored.ValidationPassed, stored.SourceSizeBytes, stored.OutputSizeBytes, stored.PercentageSaved).Should().Be((true, 1000L, 400L, 60m));
        (stored.FailureCategory, stored.FailureMessage, stored.CompletedUtc).Should().Be((null, null, null));
        (await new OutputManifestStore().LoadAsync(job.OutputPath!)).Validation!.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task AFailListsTheReasonsFailsTheJobAndRecordsThemInTheManifest()
    {
        var job = await CreateReadyJobAsync();
        _validation.Report = new ValidationReport { Passed = false, Failures = ["Duration differs.", "Audio track is missing."] };

        var (code, stdout, stderr) = await RunAsync(job.Id.ToString("N"));

        code.Should().Be((int)ExitCode.ValidationFailure);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be("Validation failed: Duration differs.; Audio track is missing.\n");
        var stored = (await _repository.GetAsync(DatabasePath, job.Id))!;
        stored.Status.Should().Be(JobStatus.Failed);
        stored.ValidationPassed.Should().BeFalse();
        stored.FailureCategory.Should().Be("ValidationFailed");
        stored.FailureMessage.Should().Be("Duration differs.; Audio track is missing.");
        stored.CompletedUtc.Should().NotBeNull();
        (await new OutputManifestStore().LoadAsync(job.OutputPath!)).Validation!.Failures.Should().Equal("Duration differs.", "Audio track is missing.");
    }

    [Fact]
    public async Task WarningsKeepTheJobReadyPrintEachOneAndStoreThem()
    {
        var job = await CreateReadyJobAsync();
        _validation.Report = new ValidationReport { Passed = true, Warnings = ["The AV1's video is 2 s shorter than the original's.", "The AV1 failed the decode test: bad frame"], SourceSizeBytes = 1000, OutputSizeBytes = 400, PercentageSaved = 60m };

        var (code, stdout, stderr) = await RunAsync(job.Id.ToString("N"));

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be("""
            Validation passed with warnings: saved 60.0%. Check the AV1 before finalising it:
              The AV1's video is 2 s shorter than the original's.
              The AV1 failed the decode test: bad frame

            """.ReplaceLineEndings("\n"));
        var stored = (await _repository.GetAsync(DatabasePath, job.Id))!;
        stored.Status.Should().Be(JobStatus.ReadyToFinalize);
        stored.ValidationWarnings.Should().Equal(_validation.Report.Warnings);
        (await new OutputManifestStore().LoadAsync(job.OutputPath!)).Validation!.Warnings.Should().Equal(_validation.Report.Warnings);
    }

    [Fact]
    public async Task NotWorthConvertingSkipsTheJobDeletesTheAv1AndExitsZero()
    {
        var job = await CreateReadyJobAsync();
        _validation.Report = new ValidationReport { Passed = false, NotWorthConverting = "the AV1 saves only 10 B, less than 1MiB (savings.minimumBytesSaved).", SourceSizeBytes = 1000, OutputSizeBytes = 990 };

        var (code, stdout, stderr) = await RunAsync(job.Id.ToString("N"));

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be("Not worth converting: the AV1 saves only 10 B, less than 1MiB (savings.minimumBytesSaved). The AV1 was deleted; the original stays.\n");
        var stored = (await _repository.GetAsync(DatabasePath, job.Id))!;
        (stored.Status, stored.FailureCategory).Should().Be((JobStatus.Skipped, FailureCategories.NotWorthConverting));
        stored.QualityFingerprint.Should().NotBeNull("discovery checks the video again once the settings change");
        File.Exists(job.OutputPath).Should().BeFalse();
        File.Exists(new OutputManifestStore().GetPath(job.OutputPath!)).Should().BeFalse();
        File.Exists(job.SourcePath).Should().BeTrue();
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string jobId)
    {
        var config = await RunCli.WriteConfigAsync(DatabasePath);
        return await ConsoleCapture.RunCliAsync(services => services.AddSingleton<IOutputValidationService>(_validation), "validate", jobId, "--config", config);
    }

    /// <summary>A job ready to finalise with its files and manifest on disk, then moved through <paramref name="after"/> as a later stage would.</summary>
    private async Task<JobRecord> CreateReadyJobAsync(bool withOutput = true, JobStatus[]? after = null)
    {
        var source = Path.Combine(_directory, "clip-a.mp4");
        var output = Path.Combine(_directory, "clip-a.encoding.mp4");
        await File.WriteAllTextAsync(source, "original");
        await File.WriteAllTextAsync(output, "av1");
        await new OutputManifestStore().SaveAsync(new OutputManifest { SourcePath = source, OutputPath = output, Validation = new ValidationReport { Passed = true } });
        var job = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = source, Status = JobStatus.Queued, OutputPath = withOutput ? output : null });
        foreach (var status in new[] { JobStatus.Validating, JobStatus.ReadyToFinalize }.Concat(after ?? []))
        {
            job.Status = status;
            await _repository.UpdateAsync(DatabasePath, job);
        }
        return job;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class FakeValidation : IOutputValidationService
    {
        public ValidationReport Report { get; set; } = new() { Passed = true };
        public int Calls { get; private set; }

        public Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Report);
        }
    }
}
