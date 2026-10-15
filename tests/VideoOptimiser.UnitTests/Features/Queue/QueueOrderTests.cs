using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Cleanup;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests.Features.Queue;

/// <summary>
/// A line that reports a completed write reaches the console before any later read that could throw, so a user whose drop
/// succeeded never sees only an error and believes it failed.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class QueueOrderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    public QueueOrderTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    [Fact]
    public async Task ADropThatSucceededIsReportedEvenWhenCleanupsPlanCannotBeRead()
    {
        var source = Path.Combine(_directory, "clip-a.mp4");
        await File.WriteAllTextAsync(source, "original");
        var job = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = "original", Status = JobStatus.Queued });
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, $"database:\n  path: '{DatabasePath}'\n");

        var (code, stdout, stderr) = await ConsoleCapture.RunCliAsync(services => services.AddSingleton<ITemporaryOutputCleanupService>(new UnreadablePlan()), "queue", "drop", job.Id.ToString("N"), "--config", config);

        code.Should().NotBe(0);
        stdout.Should().StartWith("Dropped clip-a.mp4." + Environment.NewLine);
        stderr.Should().Contain("plan unreadable");
        (await _repository.GetAsync(DatabasePath, job.Id))!.Status.Should().Be(JobStatus.Cancelled);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class UnreadablePlan : ITemporaryOutputCleanupService
    {
        public Task<TemporaryOutputCleanupPlan> FindAsync(string databasePath, string immichStagingDirectory, CancellationToken cancellationToken = default) =>
            throw new IOException("plan unreadable");

        public TemporaryOutputCleanupResult Delete(TemporaryOutputCleanupPlan plan) => throw new NotSupportedException();
    }
}
