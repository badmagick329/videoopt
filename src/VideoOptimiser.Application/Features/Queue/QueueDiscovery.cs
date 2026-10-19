using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Queue;

/// <summary>
/// <c>queue add</c>: finds the videos worth converting and puts them in the queue, all of them or one file. The locks come
/// in only to say what an already open job is doing now (<see cref="OpenJobState"/>).
/// </summary>
public sealed class QueueDiscovery(IFileScanner scanner, IJobRepository jobs, IFileFingerprintService fingerprints, IDatabaseLocks locks) : IQueueDiscovery
{
    public QueueDiscoveryRefusal? Refusal(AppSettings settings) =>
        settings.Watch.Roots.Count == 0 ? QueueDiscoveryRefusal.NoWatchRoots : null;

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
        int existing = 0, held = 0, ready = 0, alreadyConverted = 0, previouslyFailed = 0, previouslySkipped = 0, previouslyRejected = 0;
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
                else if (settler.Status == JobStatus.Rejected) previouslyRejected++;
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
                var remote = string.Equals(settings.Processing.Mode, ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase);
                await jobs.CreateAsync(databasePath, new JobRecord
                {
                    Id = id,
                    SourcePath = Path.GetFullPath(item.Path),
                    SourceFingerprint = await fingerprints.CreateAsync(item.Path, cancellationToken),
                    Status = JobStatus.Queued,
                    // An eligible item always carries the probe it was judged on, cached or fresh.
                    VideoFacts = item.MediaInfo!.ToVideoFacts(item.SizeBytes!.Value),
                    ExecutionMode = remote ? ProcessingModes.RemoteSsh : ProcessingModes.Local,
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

        return new QueueDiscoveryResult(queued, existing, issues, report.CacheHits, report.RealProbes, alreadyConverted, previouslyFailed, previouslySkipped, held, ready, previouslyRejected);
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
        if (ToolFiles.IsMadeByTool(path, settings)) return new QueueAddResult(QueueAddOutcome.MadeByTool, path);
        if (FileScanner.IsWithinArchiveDirectory(path, settings.Original.ArchiveDirectory)) return new QueueAddResult(QueueAddOutcome.Archived, path);
        if (await jobs.FindOpenBySourceAsync(databasePath, path, cancellationToken) is { } open) return OpenJob(databasePath, path, open);
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
        JobRecord created;
        try
        {
            created = await jobs.CreateAsync(databasePath, job, cancellationToken);
        }
        catch (OpenJobExistsException)
        {
            // Another command queued it since the check above.
            return OpenJob(databasePath, path, await jobs.FindOpenBySourceAsync(databasePath, path, cancellationToken) ?? throw new JobChangedException(job.Id));
        }

        return new QueueAddResult(QueueAddOutcome.Queued, path, created, OutsideRules: outsideRules);
    }

    public async Task<int> AheadOfAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default) =>
        (await jobs.ListAsync(databasePath, terminal: false, cancellationToken))
            .Count(other => other.Id != job.Id && other.IsQueued && !other.Held && other.ResumeStatus != JobStatus.Finalizing);

    public NextRun NextRun(string databasePath) => NextRunRule.Decide(locks.LiveRun(databasePath)?.Command);

    public async Task<IReadOnlyList<OpenJobEntry>> OpenJobsAsync(string databasePath, CancellationToken cancellationToken = default) =>
        OpenJobList.Group(await jobs.ListAsync(databasePath, terminal: false, cancellationToken), locks.LiveRun(databasePath) is not null, locks.IsFinaliseGoing(databasePath));

    private QueueAddResult OpenJob(string databasePath, string path, JobRecord open) =>
        new(QueueAddOutcome.OpenJob, path, open, OpenState: OpenStateOf(open, locks.LiveRun(databasePath) is not null, locks.IsFinaliseGoing(databasePath)));

    /// <summary>The open status in the glossary's terms; a converting status with no run going is a stop a crash left.</summary>
    internal static OpenJobState OpenStateOf(JobRecord job, bool runGoing, bool finaliseGoing) => job switch
    {
        { Status: JobStatus.ReadyToFinalize, FailureMessage: not null } => OpenJobState.LastFinaliseFailed,
        { Held: true } => OpenJobState.Held,
        { Status: JobStatus.ReadyToFinalize } => OpenJobState.ReadyToFinalize,
        { Status: JobStatus.Interrupted, ResumeStatus: JobStatus.Finalizing } => OpenJobState.StoppedWhileFinalising,
        { Status: JobStatus.Finalizing } => finaliseGoing ? OpenJobState.Finalising : OpenJobState.StoppedWhileFinalising,
        _ when job.IsConverting && !runGoing => OpenJobState.Stopped,
        _ => OpenJobState.Other
    };

    private static bool IsWithinFolder(string path, string folder) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Originals discovery leaves alone because their latest job settled them, mapped to that job's status. A completed job
    /// means <c>original.action: keep</c> left the original in place; a failed, cancelled or rejected one means converting it again
    /// would repeat the same encode and fail the same way, or undo the user's cancel or reject, so only <c>queue retry</c> requeues it.
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
}
