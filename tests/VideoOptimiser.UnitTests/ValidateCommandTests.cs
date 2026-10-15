using FluentAssertions;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// F7 (VOPT-60): validate re-checks only a job on its way to finalising. A failed validation fails the job, and cleanup then
/// deletes its AV1, so a job whose files are missing, or that has already ended, is refused and left as it is.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class ValidateCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

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
}
