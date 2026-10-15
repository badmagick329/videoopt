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
/// deletes its AV1, so a job whose files are missing, or that has already ended, is refused and left as it is. The first two
/// cases run the real validation; the rest fake it to pin what validate prints, returns and records for a pass and a fail.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class ValidateCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeValidation _validation = new();

    public ValidateCommandTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    [Fact]
    public async Task AFinishedJobIsRefusedAndItsValidationRecordLeftAlone()
    {
        var (config, job, output) = await ArrangeAsync(JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing, JobStatus.Completed);
        var manifestPath = new OutputManifestStore().GetPath(output);
        var before = await File.ReadAllTextAsync(manifestPath);

        var (code, _, error) = await ConsoleCapture.RunCliAsync("validate", job.Id.ToString("N"), "--config", config);

        code.Should().Be((int)ExitCode.ProcessingFailure);
        error.Trim().Should().Be($"validate re-checks a job that is ready to finalise; {job.Id:N} is Completed.");
        (await File.ReadAllTextAsync(manifestPath)).Should().Be(before);
    }

    [Fact]
    public async Task AMissingOriginalIsReportedAndTheJobLeftAsItIs()
    {
        var (config, job, _) = await ArrangeAsync(JobStatus.Validating);
        File.Delete(job.SourcePath);

        var (code, _, error) = await ConsoleCapture.RunCliAsync("validate", job.Id.ToString("N"), "--config", config);

        code.Should().Be((int)ExitCode.ProcessingFailure);
        error.Trim().Should().Be($"Original does not exist: {job.SourcePath}.");
        (await _repository.GetAsync(DatabasePath, job.Id))!.Status.Should().Be(JobStatus.Validating);
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

    [Fact]
    public async Task AJobThatIsNotInTheDatabaseIsNotFound()
    {
        var (code, stdout, stderr) = await RunAsync(Guid.NewGuid().ToString("N"));

        code.Should().Be((int)ExitCode.ProcessingFailure);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be("Job or temporary output was not found.\n");
        _validation.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AReadyJobWithoutAnOutputPathIsNotFoundEither()
    {
        var job = await CreateReadyJobAsync(withOutput: false);

        var (code, _, stderr) = await RunAsync(job.Id.ToString("N"));

        code.Should().Be((int)ExitCode.ProcessingFailure);
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
        stdout.ReplaceLineEndings("\n").Should().Be("Validation passed: saved 60.0%\n");
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

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string jobId)
    {
        var config = Path.Combine(_directory, "config.yaml");
        File.WriteAllText(config, $"database:\n  path: '{DatabasePath}'\n");
        return ConsoleCapture.RunCliAsync(services => services.AddSingleton<IOutputValidationService>(_validation), "validate", jobId, "--config", config);
    }

    private async Task<JobRecord> CreateReadyJobAsync(bool withOutput = true)
    {
        var source = Path.Combine(_directory, "clip-a.mp4");
        var output = Path.Combine(_directory, "clip-a.encoding.mp4");
        await File.WriteAllTextAsync(source, "original");
        await File.WriteAllTextAsync(output, "av1");
        await new OutputManifestStore().SaveAsync(new OutputManifest { SourcePath = source, OutputPath = output, Validation = new ValidationReport { Passed = true } });
        var job = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = source, Status = JobStatus.Queued, OutputPath = withOutput ? output : null });
        foreach (var status in new[] { JobStatus.Validating, JobStatus.ReadyToFinalize })
        {
            job.Status = status;
            await _repository.UpdateAsync(DatabasePath, job);
        }
        return job;
    }

    private async Task<(string Config, JobRecord Job, string Output)> ArrangeAsync(params JobStatus[] path)
    {
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, $"database:\n  path: '{DatabasePath}'\n");
        var source = Path.Combine(_directory, "movie.mp4");
        var output = Path.Combine(_directory, "movie.encoding.mp4");
        await File.WriteAllTextAsync(source, "original");
        await File.WriteAllTextAsync(output, "av1");
        await new OutputManifestStore().SaveAsync(new OutputManifest { SourcePath = source, OutputPath = output, Validation = new ValidationReport { Passed = true } });
        var job = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = source, Status = JobStatus.Queued, OutputPath = output });
        foreach (var status in path)
        {
            job.Status = status;
            await _repository.UpdateAsync(DatabasePath, job);
        }
        return (config, job, output);
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
