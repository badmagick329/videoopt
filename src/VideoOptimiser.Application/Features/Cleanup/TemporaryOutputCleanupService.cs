using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Cleanup;

/// <summary>
/// Finds abandoned temporary outputs in the <c>.video-optimiser</c> folder beside each original the database has a job
/// for. Only the top level of those folders is read, and only files whose names identify a job attempt are considered.
/// Also finds staged Immich originals that no job will read again: finalisation deletes a completed job's staging, but a
/// failed or cancelled job's copy, often gigabytes, would otherwise stay forever.
/// </summary>
public sealed class TemporaryOutputCleanupService(IJobRepository jobs, IOnlyCopyGuard onlyCopy) : ITemporaryOutputCleanupService
{
    public async Task<TemporaryOutputCleanupPlan> FindAsync(string databasePath, ImmichSettings immich, CancellationToken cancellationToken = default)
    {
        var (openJobs, allJobs) = await ReadJobsAsync(databasePath, cancellationToken);
        var folders = allJobs.Values
            .Select(job => Path.Combine(Path.GetDirectoryName(job.SourcePath)!, TemporaryOutputClassifier.FolderName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists);

        var abandoned = new List<AbandonedTemporaryOutput>();
        var unknown = new List<string>();
        foreach (var folder in folders)
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
            {
                if (!TemporaryOutputClassifier.TryParse(file.Name, out var jobId, out var attempt)) continue;
                if (!allJobs.TryGetValue(jobId, out var job))
                {
                    unknown.Add(file.FullName);
                    continue;
                }

                var reason = TemporaryOutputClassifier.AbandonedReason(job, attempt);
                if (reason is not null) abandoned.Add(new AbandonedTemporaryOutput(file.FullName, jobId, attempt, file.Length, reason));
            }
        }

        var staged = FindStagedOriginals(allJobs.Values, openJobs, immich.StagingDirectory);
        var kept = new List<KeptOnlyCopy>();
        var risks = new Dictionary<Guid, OnlyCopyRisk?>();
        var deletableAbandoned = new List<AbandonedTemporaryOutput>();
        foreach (var file in abandoned)
        {
            // Only a job's finished AV1 can be the only copy; its manifest and the partial files of other attempts are not.
            var job = allJobs[file.JobId];
            var risk = IsOutputOf(job, file.Path) ? await RiskOfAsync(job) : null;
            if (risk is null) deletableAbandoned.Add(file);
            else kept.Add(new KeptOnlyCopy(file.Path, file.JobId, IsAv1: true, risk));
        }

        var deletableStaged = new List<StagedImmichOriginal>();
        foreach (var file in staged)
        {
            var risk = await RiskOfAsync(allJobs[file.JobId]);
            if (risk is null) deletableStaged.Add(file);
            else kept.Add(new KeptOnlyCopy(file.Path, file.JobId, IsAv1: false, risk));
        }

        return new TemporaryOutputCleanupPlan(
            deletableAbandoned.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            deletableStaged,
            unknown.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            kept.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray());

        // A completed job's leftovers are temporary files: finalise installed its AV1. For any other job the guard decides,
        // and it is asked once per job whatever number of its files are in the plan, since each ask may be a call to Immich.
        async Task<OnlyCopyRisk?> RiskOfAsync(JobRecord job)
        {
            if (job.Status == JobStatus.Completed) return null;
            if (!risks.TryGetValue(job.Id, out var risk)) risks[job.Id] = risk = await onlyCopy.CheckAsync(immich, job, cancellationToken);
            return risk;
        }
    }

    private static bool IsOutputOf(JobRecord job, string path) =>
        job.OutputPath is { } output && string.Equals(Path.GetFullPath(output), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A staged original is deleted only when the latest job for its path has ended and no open job reads that path or
    /// that asset: a retry's queued job shares the path, and deleting its copy would make it download again. Outputs of a
    /// failed finalisation are kept for manual review, so their original is too.
    /// </summary>
    private static StagedImmichOriginal[] FindStagedOriginals(IEnumerable<JobRecord> allJobs, IReadOnlyList<JobRecord> openJobs, string stagingDirectory)
    {
        var staged = new List<StagedImmichOriginal>();
        foreach (var (job, path, reason) in DeletableStagedPaths(allJobs, openJobs))
        {
            if (!ImmichStaging.IsStagedOriginal(stagingDirectory, job.ImmichAssetId, path)) continue;
            var file = new FileInfo(path);
            if (file.Exists) staged.Add(new StagedImmichOriginal(path, job.Id, file.Length, reason));
        }

        return staged.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>The job rules of <see cref="FindStagedOriginals"/>, without the file checks, so a delete can apply them to the jobs as they are now.</summary>
    private static IEnumerable<(JobRecord Job, string Path, string Reason)> DeletableStagedPaths(IEnumerable<JobRecord> allJobs, IReadOnlyList<JobRecord> openJobs)
    {
        var openPaths = openJobs.Select(job => Path.GetFullPath(job.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var openAssets = openJobs.Select(job => job.ImmichAssetId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var job in allJobs
                     .Where(job => job.SourceKind == JobSourceKind.Immich)
                     .GroupBy(job => Path.GetFullPath(job.SourcePath), StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.MaxBy(job => job.CreatedUtc)!))
        {
            var path = Path.GetFullPath(job.SourcePath);
            if (openPaths.Contains(path) || (job.ImmichAssetId is { } assetId && openAssets.Contains(assetId))) continue;
            if (job.FailureCategory == FailureCategories.FinalizationFailed) continue;
            var reason = job.Status switch
            {
                JobStatus.Completed => "staged Immich original; job completed",
                JobStatus.Failed => "staged Immich original; job failed",
                JobStatus.Cancelled => "staged Immich original; job cancelled",
                JobStatus.Skipped => "staged Immich original; job skipped",
                JobStatus.Rejected => "staged Immich original; job rejected",
                _ => null
            };
            if (reason is not null) yield return (job, path, reason);
        }
    }

    /// <summary>
    /// <c>cleanup</c> shows the plan and then waits at a y/N for as long as the user takes, holding no lock: the finalise lock
    /// would block a run for that long. Meanwhile a run's validation recheck can turn a failed job ready to finalise, and
    /// <c>queue retry</c> can open a job on a staged copy's path, so each file is checked again against its job as it is
    /// now. The only-copy guard is not asked again: it judges the original, not the job's status, so a status change alone
    /// cannot turn its answer.
    /// </summary>
    public async Task<TemporaryOutputCleanupResult> DeleteAsync(string databasePath, TemporaryOutputCleanupPlan plan, CancellationToken cancellationToken = default)
    {
        var (openJobs, allJobs) = await ReadJobsAsync(databasePath, cancellationToken);
        var stagedJobs = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var (job, path, _) in DeletableStagedPaths(allJobs.Values, openJobs)) stagedJobs[path] = job.Id;
        var abandoned = plan.Abandoned
            .Where(file => allJobs.TryGetValue(file.JobId, out var job) && TemporaryOutputClassifier.AbandonedReason(job, file.Attempt) is not null)
            .Select(file => (file.Path, file.SizeBytes));
        var staged = plan.StagedOriginals
            .Where(file => stagedJobs.TryGetValue(file.Path, out var jobId) && jobId == file.JobId)
            .Select(file => (file.Path, file.SizeBytes))
            .ToArray();
        var deletable = abandoned.Concat(staged).ToArray();
        var jobChanged = plan.Abandoned.Select(file => file.Path)
            .Concat(plan.StagedOriginals.Select(file => file.Path))
            .Except(deletable.Select(file => file.Path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var deleted = 0;
        var deletedBytes = 0L;
        var failures = new List<string>();
        foreach (var (path, sizeBytes) in deletable)
        {
            try
            {
                File.Delete(path);
                deleted++;
                deletedBytes += sizeBytes;
            }
            // An ab-av1 left running by a crashed controller can still hold the file open.
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{path}: {exception.Message}");
            }
        }

        // The asset folder goes once nothing is left in it; one still holding a kept output or a stray file stays.
        foreach (var folder in staged.Select(file => Path.GetDirectoryName(file.Path)!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DeleteIfEmpty(Path.Combine(folder, TemporaryOutputClassifier.FolderName));
            DeleteIfEmpty(folder);
        }

        return new TemporaryOutputCleanupResult(deleted, deletedBytes, failures, jobChanged);
    }

    private async Task<(IReadOnlyList<JobRecord> Open, Dictionary<Guid, JobRecord> All)> ReadJobsAsync(string databasePath, CancellationToken cancellationToken)
    {
        var openJobs = await jobs.ListAsync(databasePath, terminal: false, cancellationToken);
        var allJobs = openJobs
            .Concat(await jobs.ListAsync(databasePath, terminal: true, cancellationToken))
            .ToDictionary(job => job.Id);
        return (openJobs, allJobs);
    }

    private static void DeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Something appeared in it or holds it open; an empty folder left behind costs nothing.
        }
    }
}
