using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Immich;

/// <summary>
/// Downloads again the staged original of each open Immich job that lost it: a job <c>queue retry</c> created after
/// <c>cleanup</c> deleted the copy, or one whose copy was deleted by hand. Such a job could never run, and discovery
/// skips an asset with an open job, so nothing else would bring the file back. The download must match the job's
/// checksum, so the job converts the bytes it was created for. When Immich no longer returns them, the job fails as
/// <c>SourceMissing</c>, which leaves the asset free for discovery. <c>immich run</c> passes the open jobs its limit takes;
/// <c>run</c> passes the jobs it is about to convert. Only Immich's answer that the asset is gone (HTTP 400 or 404) or
/// holds other bytes fails a job; any other failure means Immich can't be used now, and the jobs left stay queued.
/// </summary>
public sealed class ImmichRestaging(IImmichClientFactory clients, IJobRepository jobs, Func<DateTimeOffset>? utcNow = null) : IImmichRestaging
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public async Task<ImmichRestageResult> RestageMissingAsync(string databasePath, AppSettings settings, IReadOnlyCollection<JobRecord> jobs, IProgress<ImmichProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // Owned here, so the jobs failed before Immich stopped answering are still reported.
        var failed = new List<JobRecord>();
        try
        {
            return new ImmichRestageResult(await RestageAsync(clients.Create(settings.Immich), databasePath, settings, jobs, failed, progress, cancellationToken), failed);
        }
        catch (ImmichApiException exception)
        {
            return new ImmichRestageResult(0, failed, exception.Message.TrimEnd('.'));
        }
    }

    public async Task<int> RestageAsync(IImmichClient client, string databasePath, AppSettings settings, IEnumerable<JobRecord> candidates, List<JobRecord> failed, IProgress<ImmichProgress>? progress, CancellationToken cancellationToken)
    {
        var restaged = 0;
        foreach (var job in candidates)
        {
            if (File.Exists(job.SourcePath)) continue;
            if (job.ImmichChecksum is null || !ImmichStaging.IsStagedOriginal(settings.Immich.StagingDirectory, job.ImmichAssetId, job.SourcePath))
            {
                await FailMissingAsync(databasePath, job, "The staged original is missing, and its path is not in the asset's folder under immich.stagingDirectory, so it is not downloaded again.", failed, cancellationToken);
                progress?.Report(new ImmichProgress.AssetIssue(Path.GetFileName(job.SourcePath), $"job {job.Id:N} is open but its staged copy is missing and cannot be downloaded again; the job is marked failed"));
                continue;
            }

            var path = Path.GetFullPath(job.SourcePath);
            var keep = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                progress?.Report(new ImmichProgress.Restaging(Path.GetFileName(path), job.Id));
                await client.DownloadOriginalAsync(job.ImmichAssetId!, path, cancellationToken: cancellationToken);
                if (!string.Equals(await ImmichStaging.Sha1Base64Async(path, cancellationToken), job.ImmichChecksum, StringComparison.Ordinal))
                {
                    await FailMissingAsync(databasePath, job, $"The staged original is missing, and Immich asset {job.ImmichAssetId} no longer matches the checksum this job was created for.", failed, cancellationToken);
                    progress?.Report(new ImmichProgress.Restaged(Path.GetFileName(path), "the download no longer matches the checksum this job was created for; the job is marked failed"));
                    continue;
                }

                keep = true;
                restaged++;
                progress?.Report(new ImmichProgress.Restaged(Path.GetFileName(path), null));
            }
            // The asset is gone. A server error or a rate limit says nothing about the asset, so it ends the restaging instead.
            catch (ImmichApiException exception) when (exception.StatusCode is 400 or 404)
            {
                await FailMissingAsync(databasePath, job, $"The staged original is missing, and Immich did not return asset {job.ImmichAssetId} again: {exception.Message}", failed, cancellationToken);
                progress?.Report(new ImmichProgress.Restaged(Path.GetFileName(path), $"Immich did not return the video again ({exception.Message.TrimEnd('.')}); the job is marked failed"));
            }
            finally
            {
                if (!keep) ImmichStaging.DeleteStaged(path);
            }
        }

        return restaged;
    }

    private async Task FailMissingAsync(string databasePath, JobRecord job, string message, List<JobRecord> failed, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Failed;
        job.ResumeStatus = null;
        job.FailureCategory = FailureCategories.SourceMissing;
        job.FailureMessage = message;
        job.CompletedUtc = _utcNow();
        try
        {
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            failed.Add(job);
        }
        catch (JobChangedException)
        {
            // Held or dropped from another terminal since it was read; that change stands, and the run passes over it.
        }
    }
}
