using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Infrastructure.Jobs;

/// <summary>
/// Finds abandoned temporary outputs in the <c>.video-optimiser</c> folder beside each original the database has a job
/// for. Only the top level of those folders is read, and only files whose names identify a job attempt are considered.
/// </summary>
public sealed class TemporaryOutputCleanupService(IJobRepository jobs) : ITemporaryOutputCleanupService
{
    public async Task<TemporaryOutputCleanupPlan> FindAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var allJobs = (await jobs.ListAsync(databasePath, terminal: false, cancellationToken))
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
            unknown.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public TemporaryOutputCleanupResult Delete(TemporaryOutputCleanupPlan plan)
    {
        var deleted = 0;
        var deletedBytes = 0L;
        var failures = new List<string>();
        foreach (var file in plan.Abandoned)
        {
            try
            {
                File.Delete(file.Path);
                deleted++;
                deletedBytes += file.SizeBytes;
            }
            // An ab-av1 left running by a crashed controller can still hold the file open.
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{file.Path}: {exception.Message}");
            }
        }

        return new TemporaryOutputCleanupResult(deleted, deletedBytes, failures);
    }
}
