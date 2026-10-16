using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Run;

public sealed class QueueService(IJobRepository jobs, IJobProcessor processor, IDatabaseLocks locks, ICommandHints hints) : IQueueService
{
    public async Task<QueuePreparationResult> PrepareAsync(string databasePath, AppSettings settings, JobSourceKind? sourceKind, bool modeOverridden = false, CancellationToken cancellationToken = default)
    {
        // Held for the whole preparation, so a finalisation cannot start between reading a job and writing it back; finalize
        // waits for it. Held by a finalisation instead, its jobs are live and this run leaves every finalisation alone.
        using var finalizeLock = locks.TryHoldFinalizeWhilePreparing(databasePath);
        var finalizing = finalizeLock is null;
        await jobs.MarkActiveJobsInterruptedAsync(databasePath, includeFinalizing: !finalizing, cancellationToken);
        var open = await jobs.ListAsync(databasePath, terminal: false, cancellationToken);
        var held = open.Count(job => job.Held && (sourceKind is null || job.SourceKind == sourceKind));
        // Held jobs are left out before anything can write them: nothing adopts or fails a job behind the user's back.
        var candidates = JobRunOrder.Sort(open.Where(job => job.Status is JobStatus.Queued or JobStatus.Interrupted && !job.Held)).ToArray();
        var runnable = new List<JobRecord>();
        var failed = new List<FailedQueueJob>();
        var skipped = new List<JobRecord>();
        var interruptedFinalizations = new List<JobRecord>();
        foreach (var job in candidates)
        {
            // Changed since the list was read: a queue verb from another terminal got there first, and its change stands.
            try
            {
                if (finalizing && job.ResumeStatus == JobStatus.Finalizing) continue;
                if (FinalizationRules.IsInterruptedImmich(job))
                {
                    FinalizationRules.ReturnToReady(job, FinalizationRules.InterruptedMessage(hints.Command("immich finalize")));
                    await jobs.UpdateAsync(databasePath, job, cancellationToken);
                    continue;
                }

                if (sourceKind is not null && job.SourceKind != sourceKind) continue;
                if (FinalizationRules.IsInterruptedFile(job))
                {
                    interruptedFinalizations.Add(job);
                    continue;
                }

                var compatibility = JobCompatibility.Evaluate(job, settings);
                if (compatibility.Status == JobCompatibilityStatus.Adopted)
                {
                    JobCompatibility.Apply(job, compatibility.ExpectedBinding);
                    await jobs.UpdateAsync(databasePath, job, cancellationToken);
                }
                else if (modeOverridden && JobCompatibility.IsBoundToOtherMode(job, compatibility))
                {
                    skipped.Add(job);
                    continue;
                }
                else if (compatibility.Status == JobCompatibilityStatus.Incompatible)
                {
                    job.Status = JobStatus.Failed;
                    job.FailureCategory = JobCompatibility.RemoteConfigurationChangedCategory;
                    job.FailureMessage = compatibility.Message!;
                    job.CompletedUtc = DateTimeOffset.UtcNow;
                    await jobs.UpdateAsync(databasePath, job, cancellationToken);
                    failed.Add(new FailedQueueJob(job.Id, job.SourcePath, job.FailureCategory, job.FailureMessage));
                    continue;
                }

                runnable.Add(job);
            }
            catch (JobChangedException)
            {
            }
        }

        return new QueuePreparationResult(runnable, failed, skipped, interruptedFinalizations, sourceKind, Held: held);
    }

    /// <remarks>
    /// The queue is read again before each video, so a drop, hold, release, <c>queue next</c> or new video from another
    /// terminal takes effect at the next video, and "runs next" stays true. The total in <c>[n/total]</c> is the queue as
    /// it is then, so it can change.
    /// </remarks>
    public async Task<QueueRunResult> RunAsync(string databasePath, AppSettings settings, QueuePreparationResult preparation, Func<JobRecord, CancellationToken, Task<bool>>? passOver = null, IProgress<JobProgress>? progress = null, CancellationToken stopAfterThisVideo = default, CancellationToken cancellationToken = default)
    {
        var ready = 0;
        var toCheck = 0;
        var failed = new List<FailedQueueJob>();
        var skipped = new List<SkippedQueueJob>();
        // Tried at most once each: a video that failed, was passed over or left for a later run is not picked up again.
        // The jobs preparation skipped for the other mode have said so already.
        var tried = preparation.SkippedJobs.Select(job => job.Id).ToHashSet();
        var done = 0;
        JobRecord? previous = null;
        JobRecord? passedOver = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var waiting = JobRunOrder.Sort((await jobs.ListAsync(databasePath, terminal: false, cancellationToken))
                    .Where(job => job.IsQueued && !job.Held && (preparation.SourceKind is null || job.SourceKind == preparation.SourceKind) && !tried.Contains(job.Id)))
                .ToArray();
            if (waiting.Length == 0) break;
            // A result, not an exception: the videos finished so far get their summary, and the rest stay queued.
            if (stopAfterThisVideo.IsCancellationRequested && (previous ?? passedOver) is { } last)
            {
                return new QueueRunResult(ready, failed, skipped, UnreachableWorkerJob: null, NotStarted: waiting.Length, ExitCode.Cancelled, StoppedAfter: last, ToCheck: toCheck);
            }

            var job = waiting[0];
            tried.Add(job.Id);
            if (passOver is not null && await passOver(job, cancellationToken)) continue;
            progress?.Report(JobProgress.Header(done + 1, done + waiting.Length, job.SourcePath));
            JobProcessingResult result;
            try
            {
                result = await processor.ProcessAsync(databasePath, job, settings, progress, cancellationToken);
            }
            catch (JobChangedException)
            {
                // Held or dropped from another terminal between this read and the job's first write; that change stands. It
                // counts as started, so a stop after this video pressed meanwhile does not start yet another one.
                passedOver = job;
                continue;
            }

            done++;
            if (result.Job.Status == JobStatus.ReadyToFinalize)
            {
                ready++;
                if (result.Job.ValidationWarnings.Count > 0) toCheck++;
            }
            else if (result.Job.Status == JobStatus.Skipped) skipped.Add(new SkippedQueueJob(result.Job.Id, result.Job.SourcePath, result.Job.FailureMessage ?? result.Message));
            else if (result.Job is { Status: JobStatus.Interrupted, FailureCategory: FailureCategories.RemoteUnavailable })
            {
                // Each later job would retry the dead worker three times and end the same way, so the run stops here.
                return new QueueRunResult(ready, failed, skipped, result.Job, waiting.Length - 1, QueueExitCode.Calculate(ready, failed.Count, workerUnreachable: true), ToCheck: toCheck);
            }
            else if (result.ExitCode != ExitCode.Success)
            {
                failed.Add(new FailedQueueJob(result.Job.Id, result.Job.SourcePath, result.Job.FailureCategory ?? "ProcessingFailed", result.Job.FailureMessage ?? result.Message));
            }
            previous = job;
        }

        return new QueueRunResult(ready, failed, skipped, UnreachableWorkerJob: null, NotStarted: 0, QueueExitCode.Calculate(ready, failed.Count), ToCheck: toCheck);
    }
}
