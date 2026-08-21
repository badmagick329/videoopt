using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Infrastructure.Jobs;

public sealed class JobRetryService(IJobRepository jobs) : IJobRetryService
{
    public async Task<JobRetryResult> RetryRemoteConfigurationChangedAsync(string databasePath, Guid jobId, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var original = await jobs.GetAsync(databasePath, jobId, cancellationToken);
        if (original is null) return new JobRetryResult(null, "Job was not found.");
        if (original.Status != JobStatus.Failed || !string.Equals(original.FailureCategory, JobCompatibility.RemoteConfigurationChangedCategory, StringComparison.Ordinal))
        {
            return new JobRetryResult(null, "Only jobs failed because their remote configuration changed can be retried.");
        }
        var openJob = await jobs.FindOpenBySourceAsync(databasePath, original.SourcePath, cancellationToken);
        if (openJob is not null)
        {
            return new JobRetryResult(null, $"An open job already exists for this source: {openJob.Id:N} ({openJob.Status}).");
        }

        var replacement = new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = original.SourcePath,
            SourceFingerprint = string.Empty,
            Status = JobStatus.Queued
        };
        JobCompatibility.Apply(replacement, JobCompatibility.ExpectedBinding(replacement.Id, settings));
        await jobs.CreateAsync(databasePath, replacement, cancellationToken);
        return new JobRetryResult(replacement, null);
    }
}
