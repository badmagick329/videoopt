using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Infrastructure.Jobs;

public sealed class QueueService(IFileScanner scanner, IJobRepository jobs, IFileFingerprintService fingerprints, IJobProcessor processor, IDatabaseLocks locks) : IQueueService
{
    public async Task<QueueDiscoveryResult> DiscoverAsync(string databasePath, AppSettings settings, bool first, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var openJobs = new Dictionary<string, JobRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in await jobs.ListAsync(databasePath, terminal: false, cancellationToken)) openJobs.TryAdd(Path.GetFullPath(job.SourcePath), job);
        var open = openJobs.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settled = await FindSettledOriginalsAsync(databasePath, open, QualityFingerprint.Compute(settings), onlyPath: null, cancellationToken);
        var report = await scanner.ScanAsync(
            settings.Watch.Roots,
            settings,
            stopAfterFirstEligible: first,
            openSourcePaths: first ? open.Union(settled.Keys, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase) : null,
            progress: progress,
            cancellationToken: cancellationToken);
        var queued = new List<string>();
        var issues = report.Issues.Count;
        int existing = 0, held = 0, ready = 0, alreadyConverted = 0, previouslyFailed = 0, previouslySkipped = 0;
        // R10: a held or finished video is not one a run will take, so it is counted apart and the next command matches.
        void CountOpen(JobRecord job)
        {
            if (job.Held) held++;
            else if (IsReadyToFinalize(job)) ready++;
            else existing++;
        }

        foreach (var item in report.Items.Where(item => item.Status == ScanItemStatus.Eligible))
        {
            // With --first the scan stops at the first file it can queue, so every file before it is still counted.
            if (first && queued.Count > 0)
            {
                break;
            }

            var path = Path.GetFullPath(item.Path);
            if (settled.TryGetValue(path, out var settler))
            {
                if (settler.Status == JobStatus.Completed) alreadyConverted++;
                else if (settler.Status == JobStatus.Skipped) previouslySkipped++;
                else previouslyFailed++;
                continue;
            }

            try
            {
                if (openJobs.TryGetValue(path, out var openJob) || (openJob = await jobs.FindOpenBySourceAsync(databasePath, item.Path, cancellationToken)) is not null)
                {
                    CountOpen(openJob);
                    continue;
                }

                var id = Guid.NewGuid();
                var remote = string.Equals(settings.Processing.Mode, "remoteSsh", StringComparison.OrdinalIgnoreCase);
                await jobs.CreateAsync(databasePath, new JobRecord
                {
                    Id = id,
                    SourcePath = Path.GetFullPath(item.Path),
                    SourceFingerprint = await fingerprints.CreateAsync(item.Path, cancellationToken),
                    Status = JobStatus.Queued,
                    // An eligible item always carries the probe it was judged on, cached or fresh.
                    VideoFacts = item.MediaInfo!.ToVideoFacts(item.SizeBytes!.Value),
                    ExecutionMode = remote ? "remoteSsh" : "local",
                    RemoteHost = remote ? RemoteExecutionIdentity.Host(settings.Processing.RemoteSsh) : null,
                    RemoteWorkspace = remote ? $"{settings.Processing.RemoteSsh.WorkingDirectory.TrimEnd('/')}/{id:N}" : null
                }, cancellationToken);
                queued.Add(item.Path);
            }
            catch (OpenJobExistsException)
            {
                // Another command queued it since the check above.
                existing++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                issues++;
            }
        }

        return new QueueDiscoveryResult(queued, existing, issues, report.CacheHits, report.RealProbes, alreadyConverted, previouslyFailed, previouslySkipped, held, ready);
    }

    /// <summary>Ready to finalise, being finalised, or stopped while it was: finalize's work, not a run's.</summary>
    private static bool IsReadyToFinalize(JobRecord job) =>
        job.Status is JobStatus.ReadyToFinalize or JobStatus.Finalizing || job is { Status: JobStatus.Interrupted, ResumeStatus: JobStatus.Finalizing };

    public async Task<QueueAddResult> AddFileAsync(string databasePath, AppSettings settings, string path, bool force, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) return new QueueAddResult(QueueAddOutcome.Missing, path);
        // Not even with --force (R10): converting a temporary output, a staged copy or an archived original would replace
        // the tool's own file, or undo a finalisation.
        if (IsMadeByTool(path, settings)) return new QueueAddResult(QueueAddOutcome.MadeByTool, path);
        if (FileScanner.IsWithinArchiveDirectory(path, settings.Original.ArchiveDirectory)) return new QueueAddResult(QueueAddOutcome.Archived, path);
        if (await jobs.FindOpenBySourceAsync(databasePath, path, cancellationToken) is { } open) return new QueueAddResult(QueueAddOutcome.OpenJob, path, open);
        var settled = await FindSettledOriginalsAsync(databasePath, [], QualityFingerprint.Compute(settings), onlyPath: path, cancellationToken);
        if (settled.TryGetValue(path, out var settler)) return new QueueAddResult(QueueAddOutcome.Settled, path, settler);
        var outsideRules = !settings.Watch.Roots.Any(root => IsWithinFolder(path, root.Path));
        if (outsideRules && !force) return new QueueAddResult(QueueAddOutcome.OutsideRoots, path);

        // Judged by the rules first even with --force, so the user hears whether --force was what queued it.
        var verdict = await scanner.JudgeAsync(path, settings, force: false, cancellationToken);
        if (force && verdict is { Item.Status: ScanItemStatus.Ineligible, TooSmall: false })
        {
            outsideRules = true;
            verdict = await scanner.JudgeAsync(path, settings, force: true, cancellationToken);
        }
        var item = verdict.Item;
        switch (item.Status)
        {
            case ScanItemStatus.Ineligible: return new QueueAddResult(verdict.TooSmall ? QueueAddOutcome.TooSmall : QueueAddOutcome.Ineligible, path, Reason: item.Reason);
            case ScanItemStatus.Unavailable: return new QueueAddResult(QueueAddOutcome.Unreadable, path, Reason: item.Reason);
            case ScanItemStatus.ProbeFailed: return new QueueAddResult(QueueAddOutcome.ProbeFailed, path, Reason: item.Reason);
        }

        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = path,
            SourceFingerprint = await fingerprints.CreateAsync(path, cancellationToken),
            Status = JobStatus.Queued,
            // An eligible item always carries the probe it was judged on, cached or fresh.
            VideoFacts = item.MediaInfo!.ToVideoFacts(item.SizeBytes!.Value),
            Forced = force
        };
        JobCompatibility.Apply(job, JobCompatibility.ExpectedBinding(job.Id, settings));
        try
        {
            return new QueueAddResult(QueueAddOutcome.Queued, path, await jobs.CreateAsync(databasePath, job, cancellationToken), OutsideRules: outsideRules);
        }
        catch (OpenJobExistsException)
        {
            // Another command queued it since the check above.
            return new QueueAddResult(QueueAddOutcome.OpenJob, path, await jobs.FindOpenBySourceAsync(databasePath, path, cancellationToken) ?? throw new JobChangedException(job.Id));
        }
    }

    private static bool IsMadeByTool(string path, AppSettings settings)
    {
        for (var directory = Path.GetDirectoryName(path); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            if (string.Equals(Path.GetFileName(directory), TemporaryOutputClassifier.FolderName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return settings.Immich.StagingDirectory.Length > 0 && IsWithinFolder(path, settings.Immich.StagingDirectory);
    }

    private static bool IsWithinFolder(string path, string folder) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Originals discovery leaves alone because their latest job settled them, mapped to that job's status. A completed job
    /// means <c>original.action: keep</c> left the original in place; a failed or cancelled one means converting it again
    /// would repeat the same encode and fail the same way, or undo the user's cancel, so only <c>queue retry</c> requeues it.
    /// A skipped job (AV1 would not save enough) settles its original only while <paramref name="qualityFingerprint"/> still
    /// matches the one that skipped it: changing the quality bar checks the video again.
    /// Either way the file is still eligible, and a matching fingerprint proves it is the one that job saw; a changed file
    /// is queued as normal. The size check first skips the fingerprint read for paths that <c>delete</c> or <c>archive</c>
    /// replaced. Paths with an open job are left to the open-job count. Each maps to the job that settled it.
    /// </summary>
    /// <param name="onlyPath">The one original to check, for <c>queue add &lt;file&gt;</c>; null for every one.</param>
    private async Task<Dictionary<string, JobRecord>> FindSettledOriginalsAsync(string databasePath, HashSet<string> open, string qualityFingerprint, string? onlyPath, CancellationToken cancellationToken)
    {
        var settled = new Dictionary<string, JobRecord>(StringComparer.OrdinalIgnoreCase);
        var latestJobs = (await jobs.ListAsync(databasePath, terminal: true, cancellationToken))
            .Where(job => onlyPath is null || string.Equals(Path.GetFullPath(job.SourcePath), onlyPath, StringComparison.OrdinalIgnoreCase))
            .GroupBy(job => Path.GetFullPath(job.SourcePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(job => job.CreatedUtc)!)
            .Where(job => !open.Contains(Path.GetFullPath(job.SourcePath)) && !FailureCategories.Requeued.Contains(job.FailureCategory ?? string.Empty))
            .Where(job => job.Status != JobStatus.Skipped || job.QualityFingerprint == qualityFingerprint);
        foreach (var job in latestJobs)
        {
            var file = new FileInfo(job.SourcePath);
            if (!file.Exists || (job.SourceSizeBytes is { } size && file.Length != size)) continue;
            try
            {
                if (await fingerprints.CreateAsync(job.SourcePath, cancellationToken) == job.SourceFingerprint) settled[Path.GetFullPath(job.SourcePath)] = job;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unreadable file cannot be queued either; discovery reports it as an issue.
            }
        }

        return settled;
    }

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
                    FinalizationRules.ReturnToReady(job, FinalizationRules.InterruptedMessage);
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
        var failed = new List<FailedQueueJob>();
        var skipped = new List<SkippedQueueJob>();
        var only = preparation.FollowQueue ? null : preparation.RunnableJobs.Select(job => job.Id).ToHashSet();
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
                    .Where(job => job.IsQueued && !job.Held && (preparation.SourceKind is null || job.SourceKind == preparation.SourceKind) && !tried.Contains(job.Id) && only?.Contains(job.Id) != false))
                .ToArray();
            if (waiting.Length == 0) break;
            // A result, not an exception: the videos finished so far get their summary, and the rest stay queued.
            if (stopAfterThisVideo.IsCancellationRequested && (previous ?? passedOver) is { } last)
            {
                return new QueueRunResult(ready, failed, skipped, UnreachableWorkerJob: null, NotStarted: waiting.Length, ExitCode.Cancelled, StoppedAfter: last);
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
            if (result.Job.Status == JobStatus.ReadyToFinalize) ready++;
            else if (result.Job.Status == JobStatus.Skipped) skipped.Add(new SkippedQueueJob(result.Job.Id, result.Job.SourcePath, result.Job.FailureMessage ?? result.Message));
            else if (result.Job is { Status: JobStatus.Interrupted, FailureCategory: FailureCategories.RemoteUnavailable })
            {
                // Each later job would retry the dead worker three times and end the same way, so the run stops here.
                return new QueueRunResult(ready, failed, skipped, result.Job, waiting.Length - 1, QueueExitCode.Calculate(ready, failed.Count, workerUnreachable: true));
            }
            else if (result.ExitCode != ExitCode.Success)
            {
                failed.Add(new FailedQueueJob(result.Job.Id, result.Job.SourcePath, result.Job.FailureCategory ?? "ProcessingFailed", result.Job.FailureMessage ?? result.Message));
            }
            previous = job;
        }

        return new QueueRunResult(ready, failed, skipped, UnreachableWorkerJob: null, NotStarted: 0, QueueExitCode.Calculate(ready, failed.Count));
    }
}
