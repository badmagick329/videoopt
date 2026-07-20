using FluentAssertions;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Jobs;

namespace VideoOptimiser.UnitTests;

public sealed class SqliteJobRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    public SqliteJobRepositoryTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "jobs.db");
    }

    [Fact]
    public async Task FindsOnlyTheActiveJobForTheSameSourceAndFingerprint()
    {
        var active = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Encoding));
        var completed = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Completed));

        var found = await _repository.FindActiveAsync(_databasePath, active.SourcePath.ToUpperInvariant(), active.SourceFingerprint);

        found!.Id.Should().Be(active.Id);
        (await _repository.ListAsync(_databasePath, terminal: true)).Should().ContainSingle(job => job.Id == completed.Id);
    }

    [Fact]
    public async Task MarksOnlyInFlightJobsInterrupted()
    {
        var encoding = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Encoding));
        var ready = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.ReadyToFinalize));

        await _repository.MarkActiveJobsInterruptedAsync(_databasePath);

        (await _repository.GetAsync(_databasePath, encoding.Id))!.Status.Should().Be(JobStatus.Interrupted);
        (await _repository.GetAsync(_databasePath, ready.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    [Fact]
    public async Task RejectsInvalidStateTransitions()
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        job.Status = JobStatus.Completed;

        var action = async () => await _repository.UpdateAsync(_databasePath, job);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static JobRecord NewJob(JobStatus status) => new()
    {
        Id = Guid.NewGuid(),
        SourcePath = "C:\\Videos\\movie.mp4",
        SourceFingerprint = "fingerprint",
        Status = status
    };
}
