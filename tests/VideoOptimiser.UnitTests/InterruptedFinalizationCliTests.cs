using FluentAssertions;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// A file job whose finalisation Ctrl+C or a crash interrupted belongs to <c>finalize</c>, which checks whether anything moved
/// (VOPT-70). The other commands leave it alone and say where it went.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class InterruptedFinalizationCliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    public InterruptedFinalizationCliTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    [Fact]
    public async Task QueueRunSaysToFinishItWithFinalizeAndLeavesItAlone()
    {
        var (config, job) = await ArrangeAsync();

        var (_, output, _) = await ConsoleCapture.RunCliAsync(CliFakes.AcceptAnySettings, "run", "--config", config);

        output.Should().Contain("movie.mp4 was interrupted while being finalised. Run 'video-optimiser finalize --ready' to finish it.");
        output.Should().NotContain("Failed jobs:");
        await ShouldBeUntouchedAsync(job);
    }

    [Fact]
    public async Task QueueDropAllLeavesItAndSaysHowToResumeIt()
    {
        var (config, job) = await ArrangeAsync();

        var (code, output, _) = await ConsoleCapture.RunCliAsync("queue", "drop", "--all", "--config", config);

        code.Should().Be(0);
        output.Should().Be($"Left 1 video stopped while being finalised; 'video-optimiser finalize {job.Id:N} --config \"{config}\"' checks and resumes it.{Environment.NewLine}");
        await ShouldBeUntouchedAsync(job);
    }

    [Fact]
    public async Task FinalizeReadyListsIt()
    {
        var (config, _) = await ArrangeAsync();

        var (_, output, _) = await ConsoleCapture.RunCliAsync("finalize", "--ready", "--config", config);

        output.Should().Contain("movie.mp4").And.Contain("Finalisation cancelled.").And.NotContain("No jobs are ready");
    }

    private async Task<(string Config, JobRecord Job)> ArrangeAsync()
    {
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, $"database:\n  path: '{DatabasePath}'\n");
        var job = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "movie.mp4"), Status = JobStatus.ReadyToFinalize, OutputPath = Path.Combine(_directory, "movie.encoding.mp4") });
        job.Status = JobStatus.Finalizing;
        await _repository.UpdateAsync(DatabasePath, job);
        await _repository.MarkActiveJobsInterruptedAsync(DatabasePath, includeFinalizing: true);
        return (config, job);
    }

    private async Task ShouldBeUntouchedAsync(JobRecord job)
    {
        var stored = (await _repository.GetAsync(DatabasePath, job.Id))!;
        stored.Status.Should().Be(JobStatus.Interrupted);
        stored.ResumeStatus.Should().Be(JobStatus.Finalizing);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
