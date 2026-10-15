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
public sealed class TemporaryOutputCleanupService(IJobRepository jobs) : ITemporaryOutputCleanupService
{
    public async Task<TemporaryOutputCleanupPlan> FindAsync(string databasePath, string immichStagingDirectory, CancellationToken cancellationToken = default)
    {
        var openJobs = await jobs.ListAsync(databasePath, terminal: false, cancellationToken);
        var allJobs = openJobs
            .Concat(await jobs.ListAsync(databasePath, terminal: true, cancellationToken))
            .ToDictionary(job => job.Id);
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

        return new TemporaryOutputCleanupPlan(
            abandoned.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            FindStagedOriginals(allJobs.Values, openJobs, immichStagingDirectory),
            unknown.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>
    /// A staged original is deleted only when the latest job for its path has ended and no open job reads that path or
    /// that asset: a retry's queued job shares the path, and deleting its copy would make it download again. Outputs of a
    /// failed finalisation are kept for manual review, so their original is too.
    /// </summary>
    private static StagedImmichOriginal[] FindStagedOriginals(IEnumerable<JobRecord> allJobs, IReadOnlyList<JobRecord> openJobs, string stagingDirectory)
    {
        var openPaths = openJobs.Select(job => Path.GetFullPath(job.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var openAssets = openJobs.Select(job => job.ImmichAssetId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staged = new List<StagedImmichOriginal>();
        foreach (var job in allJobs
                     .Where(job => job.SourceKind == JobSourceKind.Immich)
                     .GroupBy(job => Path.GetFullPath(job.SourcePath), StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.MaxBy(job => job.CreatedUtc)!))
        {
            var path = Path.GetFullPath(job.SourcePath);
            if (openPaths.Contains(path) || (job.ImmichAssetId is { } assetId && openAssets.Contains(assetId))) continue;
            if (job.FailureCategory == "FinalizationFailed") continue;
            var reason = job.Status switch
            {
                JobStatus.Completed => "staged Immich original; job completed",
                JobStatus.Failed => "staged Immich original; job failed",
                JobStatus.Cancelled => "staged Immich original; job cancelled",
                JobStatus.Skipped => "staged Immich original; job skipped",
                _ => null
            };
            if (reason is null || !ImmichStaging.IsStagedOriginal(stagingDirectory, job.ImmichAssetId, path)) continue;
            var file = new FileInfo(path);
            if (file.Exists) staged.Add(new StagedImmichOriginal(path, job.Id, file.Length, reason));
        }

        return staged.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public TemporaryOutputCleanupResult Delete(TemporaryOutputCleanupPlan plan)
    {
        var deleted = 0;
        var deletedBytes = 0L;
        var failures = new List<string>();
        foreach (var (path, sizeBytes) in plan.Abandoned.Select(file => (file.Path, file.SizeBytes))
                     .Concat(plan.StagedOriginals.Select(file => (file.Path, file.SizeBytes))))
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
        foreach (var folder in plan.StagedOriginals.Select(file => Path.GetDirectoryName(file.Path)!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DeleteIfEmpty(Path.Combine(folder, TemporaryOutputClassifier.FolderName));
            DeleteIfEmpty(folder);
        }

        return new TemporaryOutputCleanupResult(deleted, deletedBytes, failures);
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
