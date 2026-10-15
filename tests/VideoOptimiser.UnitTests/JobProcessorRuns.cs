using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// The processor by original, as tests written before it took a job call it: the original's open job, or a new queued one
/// as discovery would make, then the processor. <c>force</c> marks the job forced, as <c>queue add --force</c> will.
/// </summary>
internal static class JobProcessorRuns
{
    public static async Task<JobProcessingResult> ProcessAsync(this IJobProcessor processor, string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var job = await repository.FindOpenBySourceAsync(databasePath, Path.GetFullPath(sourcePath), CancellationToken.None) ?? await repository.CreateAsync(databasePath, NewJob(sourcePath, settings), CancellationToken.None);
        job.Forced |= force;
        return await processor.ProcessAsync(databasePath, job, settings, progress, cancellationToken);
    }

    /// <summary>Asks about the open job, or about one not written yet: the question is read-only.</summary>
    public static async Task<bool> NeedsWorkerAsync(this IJobProcessor processor, string databasePath, string sourcePath, AppSettings settings, bool force, CancellationToken cancellationToken = default)
    {
        var job = await new SqliteJobRepository(new SqliteDatabaseInitializer()).FindOpenBySourceAsync(databasePath, Path.GetFullPath(sourcePath), CancellationToken.None) ?? NewJob(sourcePath, settings);
        job.Forced |= force;
        return await processor.NeedsWorkerAsync(job, settings, cancellationToken);
    }

    private static JobRecord NewJob(string sourcePath, AppSettings settings)
    {
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.GetFullPath(sourcePath), SourceFingerprint = string.Empty, Status = JobStatus.Queued };
        JobCompatibility.Apply(job, JobCompatibility.ExpectedBinding(job.Id, settings));
        return job;
    }
}
