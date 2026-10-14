using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Infrastructure.Jobs;

/// <summary>
/// The explicit opt-in that requeues a failed, cancelled or skipped original, which discovery otherwise leaves alone while the file is
/// unchanged. The replacement starts from scratch rather than resuming, so it never reuses the earlier output or workspace.
/// </summary>
public sealed class JobRetryService(IJobRepository jobs) : IJobRetryService
{
    public async Task<JobRetryResult> RetryAsync(string databasePath, Guid jobId, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var original = await jobs.GetAsync(databasePath, jobId, cancellationToken);
        if (original is null) return new JobRetryResult(null, "Job was not found.");
        if (original.Status is not (JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped))
        {
            return new JobRetryResult(null, "Only failed, cancelled or skipped jobs can be retried.");
        }
        var openJob = await jobs.FindOpenBySourceAsync(databasePath, original.SourcePath, cancellationToken);
        if (openJob is not null)
        {
            return new JobRetryResult(null, $"An open job already exists for this original: {openJob.Id:N} ({openJob.Status}).");
        }

        // The earlier fingerprint lets discovery recognise the file again if this job is cancelled before it runs;
        // processing replaces it with the file's current fingerprint.
        var replacement = new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = original.SourcePath,
            SourceFingerprint = original.SourceFingerprint,
            Status = JobStatus.Queued,
            // Discovery and finalisation know an Immich job by its asset, not by the staged path.
            SourceKind = original.SourceKind,
            ImmichAssetId = original.ImmichAssetId,
            ImmichChecksum = original.ImmichChecksum,
            VideoFacts = original.VideoFacts,
            // Queued outside the rules once, it stays outside them: the run would otherwise fail it as ineligible.
            Forced = original.Forced
        };
        JobCompatibility.Apply(replacement, JobCompatibility.ExpectedBinding(replacement.Id, settings));
        try
        {
            await jobs.CreateAsync(databasePath, replacement, cancellationToken);
        }
        catch (OpenJobExistsException)
        {
            // Queued from another terminal since the check above.
            var raced = original.ImmichAssetId is { } assetId
                ? await jobs.FindOpenByImmichAssetAsync(databasePath, assetId, cancellationToken)
                : await jobs.FindOpenBySourceAsync(databasePath, original.SourcePath, cancellationToken);
            if (raced is null) throw;
            return new JobRetryResult(null, $"An open job already exists for this original: {raced.Id:N} ({raced.Status}).");
        }
        return new JobRetryResult(replacement, null);
    }
}
