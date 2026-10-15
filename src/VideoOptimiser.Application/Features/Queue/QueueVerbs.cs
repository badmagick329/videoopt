using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Cleanup;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Queue;

/// <summary>The queue verbs as the CLI calls them; see <see cref="QueueVerbs"/> for the rules.</summary>
public interface IQueueVerbs
{
    Task<QueueVerbOutcome> HoldAsync(string databasePath, JobRecord job, CancellationToken cancellationToken);
    Task<QueueVerbOutcome> ReleaseAsync(string databasePath, JobRecord job, CancellationToken cancellationToken);
    Task<QueueVerbOutcome> NextAsync(string databasePath, JobRecord job, CancellationToken cancellationToken);
    Task<QueueVerbOutcome> DropAsync(AppSettings settings, JobRecord job, CancellationToken cancellationToken);
    IAsyncEnumerable<DropAllStep> DropAllAsync(string databasePath, CancellationToken cancellationToken);
    Task<LeftFiles?> LeftFilesAsync(AppSettings settings, JobRecord job, bool finished, CancellationToken cancellationToken);
    Task<bool> CleanupDeletesFilesAsync(AppSettings settings, JobRecord job, CancellationToken cancellationToken);
}

/// <summary>
/// The queue verbs (VOPT-54 step 5): <c>queue hold</c>, <c>release</c>, <c>next</c> and <c>drop</c>. Each writes only while
/// the job is as it read it (the repository's compare-and-swap), so a run that started the video first wins and a verb never
/// half-applies: a lost race throws <see cref="JobChangedException"/>. A job in a converting status is converting only while
/// a run holds the run lock; otherwise a crash or a closed window left it, and <see cref="RunHoldsJobsAsync"/> marks it
/// stopped first. Drop never leaves <c>cleanup</c> the only copy of a video. These are the rules; the CLI words what they
/// decided.
/// </summary>
public sealed class QueueVerbs(IJobRepository repository, IDatabaseLocks locks, ITemporaryOutputCleanupService cleanup, IImmichClientFactory immichClients) : IQueueVerbs
{
    /// <returns><see cref="JobHeld"/>, <see cref="JobAlreadyHeld"/> or a <see cref="VerbRefused"/>.</returns>
    public async Task<QueueVerbOutcome> HoldAsync(string databasePath, JobRecord job, CancellationToken cancellationToken)
    {
        if (job.IsConverting)
        {
            if (await RunHoldsJobsAsync(databasePath, LockHolders.QueueHold, cancellationToken)) return new VerbRefused(job, VerbRefusal.ConvertingNow);
            job = await ReadAgainAsync(databasePath, job, cancellationToken);
        }
        if (job.Held) return new JobAlreadyHeld(job);
        if (!job.IsQueued) return new VerbRefused(job, NotQueuedRefusal(job, databasePath));
        if (!await repository.SetHeldAsync(databasePath, job, held: true, cancellationToken)) throw new JobChangedException(job.Id);
        return new JobHeld(job);
    }

    /// <returns><see cref="JobReleased"/> or <see cref="JobNotHeld"/>.</returns>
    public async Task<QueueVerbOutcome> ReleaseAsync(string databasePath, JobRecord job, CancellationToken cancellationToken)
    {
        // Only a waiting or stopped job is ever held, and dropping one releases it, so a held job is always one to release.
        if (!job.Held) return new JobNotHeld(job);
        if (!await repository.SetHeldAsync(databasePath, job, held: false, cancellationToken)) throw new JobChangedException(job.Id);
        return new JobReleased(job);
    }

    /// <returns><see cref="JobMovedNext"/> or a <see cref="VerbRefused"/>.</returns>
    public async Task<QueueVerbOutcome> NextAsync(string databasePath, JobRecord job, CancellationToken cancellationToken)
    {
        if (job.IsConverting)
        {
            if (await RunHoldsJobsAsync(databasePath, LockHolders.QueueNext, cancellationToken)) return new VerbRefused(job, VerbRefusal.ConvertingNow);
            job = await ReadAgainAsync(databasePath, job, cancellationToken);
        }
        if (!job.IsQueued) return new VerbRefused(job, NotQueuedRefusal(job, databasePath));
        var wasHeld = job.Held;
        // A run takes each next video from the queue as it is, so this one comes after the video converting now. An immich
        // run keeps the videos it started with (until phase A step 7), as does a run from an older version.
        var runTakesIt = NextRunRule.Decide(locks.LiveRun(databasePath)) != NextRun.AfterRunEnds;
        if (!await repository.RunNextAsync(databasePath, job, cancellationToken)) throw new JobChangedException(job.Id);
        return new JobMovedNext(job, wasHeld, runTakesIt);
    }

    /// <returns><see cref="JobDropped"/>, <see cref="JobAlreadyEnded"/>, <see cref="FinaliseRunning"/>, <see cref="OnlyCopyAtRisk"/> or a <see cref="VerbRefused"/>.</returns>
    public async Task<QueueVerbOutcome> DropAsync(AppSettings settings, JobRecord job, CancellationToken cancellationToken)
    {
        var databasePath = settings.Database.Path;
        if (job.IsConverting)
        {
            if (await RunHoldsJobsAsync(databasePath, LockHolders.QueueDrop, cancellationToken)) return new VerbRefused(job, VerbRefusal.ConvertingNow);
            job = await ReadAgainAsync(databasePath, job, cancellationToken);
        }
        // Finalize resumes these, and dropping one would leave cleanup a half-installed AV1 to delete.
        if (FinalisationRefusal(job, databasePath) is { } finalising) return new VerbRefused(job, finalising);
        if (job.IsTerminal) return new JobAlreadyEnded(job);

        var finished = job.HasFinishedAv1;
        // Under the finalise lock, so a finalisation cannot start installing the AV1 this drop hands to cleanup. One that ran
        // while this waited changed the job, and the drop's compare-and-swap refuses it.
        using var finalizeLock = finished ? await locks.TakeFinalizeAsync(databasePath, LockHolders.QueueDrop, cancellationToken) : null;
        if (finished)
        {
            if (finalizeLock is null) return new FinaliseRunning(job);
            if (await OnlyCopyRefusalAsync(settings, job, cancellationToken) is { } onlyCopy) return onlyCopy;
        }
        if (!await repository.DropAsync(databasePath, job, cancellationToken)) throw new JobChangedException(job.Id);
        return new JobDropped(job, RemoteStageOf(job), finished, job.ImmichNewAssetId);
    }

    /// <summary>
    /// What of a dropped job cleanup will delete, read from cleanup's own plan after the drop so it cannot promise more; null
    /// when nothing. Separate from <see cref="DropAsync"/> so the drop is reported first.
    /// </summary>
    /// <param name="finished">The job's <see cref="JobDropped.HadFinishedAv1"/>.</param>
    public async Task<LeftFiles?> LeftFilesAsync(AppSettings settings, JobRecord job, bool finished, CancellationToken cancellationToken) =>
        LeftFilesOf(job, finished, await CleanupPlanAsync(settings, cancellationToken));

    /// <summary>Whether cleanup would delete some of an ended job's temporary files, so the refusal can point at it.</summary>
    public async Task<bool> CleanupDeletesFilesAsync(AppSettings settings, JobRecord job, CancellationToken cancellationToken) =>
        CleanupDeletesSomeOf(job, await CleanupPlanAsync(settings, cancellationToken));

    /// <summary>
    /// Drops every waiting, stopped and held video, one step at a time as it happens. A finished AV1 is dropped by ID only,
    /// so a stopped check is left, as is a stopped finalisation, which finalize resumes.
    /// </summary>
    public async IAsyncEnumerable<DropAllStep> DropAllAsync(string databasePath, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var open = await repository.ListAsync(databasePath, terminal: false, cancellationToken);
        if (open.Any(job => job.IsConverting) && !await RunHoldsJobsAsync(databasePath, LockHolders.QueueDrop, cancellationToken))
        {
            open = await repository.ListAsync(databasePath, terminal: false, cancellationToken);
        }
        var finalising = locks.IsFinaliseGoing(databasePath);
        var drop = JobRunOrder.Sort(open.Where(job => job.IsQueued && !job.HasFinishedAv1)).ToArray();
        var stoppedFinalising = open.Where(job => job is { Status: JobStatus.Interrupted, ResumeStatus: JobStatus.Finalizing } || job.Status == JobStatus.Finalizing && !finalising).ToArray();
        var stoppedChecking = open.Where(job => job is { Status: JobStatus.Interrupted, ResumeStatus: JobStatus.Validating }).ToArray();
        if (drop.Length == 0 && stoppedFinalising.Length == 0 && stoppedChecking.Length == 0)
        {
            yield return new NothingToDrop();
            yield break;
        }

        foreach (var job in drop)
        {
            if (!await repository.DropAsync(databasePath, job, cancellationToken))
            {
                yield return new ChangedUnderDropAll(job.Id);
                continue;
            }
            yield return new DroppedInBulk(job, RemoteStageOf(job));
        }
        foreach (var job in stoppedFinalising) yield return new LeftStoppedFinalising(job);
        foreach (var job in stoppedChecking) yield return new LeftStoppedChecking(job);
    }

    private async Task<JobRecord> ReadAgainAsync(string databasePath, JobRecord job, CancellationToken cancellationToken) =>
        await repository.GetAsync(databasePath, job.Id, cancellationToken) ?? throw new JobChangedException(job.Id);

    /// <summary>
    /// R9: whether a run holds the run lock, so its converting jobs are really converting. When none does, a crash or a
    /// closed window left them, and they are marked stopped as a run's preparation marks them, under the same finalise-lock
    /// guard. The run lock is taken only for the moment this takes, and not as a run: that would start deciding the
    /// worker's fate.
    /// </summary>
    private async Task<bool> RunHoldsJobsAsync(string databasePath, string verb, CancellationToken cancellationToken)
    {
        using var runLock = locks.TryHoldRun(databasePath, verb);
        if (runLock is null) return true;
        using var finalizeLock = locks.TryHoldFinalizeWhilePreparing(databasePath);
        await repository.MarkActiveJobsInterruptedAsync(databasePath, includeFinalizing: finalizeLock is not null, cancellationToken);
        return false;
    }

    /// <summary>A finalisation, stopped or going, belongs to finalize; null for any other job.</summary>
    private VerbRefusal? FinalisationRefusal(JobRecord job, string databasePath) => job switch
    {
        { Status: JobStatus.Interrupted, ResumeStatus: JobStatus.Finalizing } => VerbRefusal.StoppedWhileFinalising,
        { Status: JobStatus.Finalizing } => locks.IsFinaliseGoing(databasePath) ? VerbRefusal.BeingFinalised : VerbRefusal.StoppedWhileFinalising,
        _ => null
    };

    private VerbRefusal NotQueuedRefusal(JobRecord job, string databasePath) => FinalisationRefusal(job, databasePath) ?? VerbRefusal.NotWaiting;

    /// <summary>
    /// W3: a remote CRF search or encode runs detached, so a stop leaves it going, and dropping the job does not stop it
    /// (<c>VOPT-90</c>).
    /// </summary>
    private static RemoteStageNote? RemoteStageOf(JobRecord job)
    {
        if (!string.Equals(job.ExecutionMode, ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase) || job.ResumeStatus is not (JobStatus.CrfSearching or JobStatus.Encoding)) return null;
        return new RemoteStageNote(job.ResumeStatus == JobStatus.Encoding ? RemoteStage.Encode : RemoteStage.CrfSearch, RemoteExecutionIdentity.IsManagedHetzner(job.RemoteHost));
    }

    private Task<TemporaryOutputCleanupPlan> CleanupPlanAsync(AppSettings settings, CancellationToken cancellationToken) =>
        cleanup.FindAsync(settings.Database.Path, settings.Immich.IsConfigured ? settings.Immich.StagingDirectory : string.Empty, cancellationToken);

    private static bool CleanupDeletesSomeOf(JobRecord job, TemporaryOutputCleanupPlan plan) =>
        plan.Abandoned.Any(file => file.JobId == job.Id) || plan.StagedOriginals.Any(file => file.JobId == job.Id);

    /// <summary>What of a dropped job cleanup will delete, read from cleanup's own plan so it cannot promise more.</summary>
    /// <param name="finished">Whether the job had a finished AV1 before the drop; any other AV1 file of it is unfinished.</param>
    private static LeftFiles? LeftFilesOf(JobRecord job, bool finished, TemporaryOutputCleanupPlan plan)
    {
        var av1Files = plan.Abandoned.Where(file => file.JobId == job.Id && !file.Path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        var finishedAv1 = finished && job.OutputPath is { } output && av1Files.Any(file => string.Equals(Path.GetFullPath(file.Path), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase));
        var av1 = av1Files.Length == 0 ? LeftAv1.None : finishedAv1 ? LeftAv1.Finished : LeftAv1.Unfinished;
        var copy = plan.StagedOriginals.Any(file => file.JobId == job.Id);
        return av1 == LeftAv1.None && !copy ? null : new LeftFiles(av1, copy);
    }

    /// <summary>
    /// R5 (W1, W2): cleanup deletes a dropped job's AV1 and downloaded copy, so a drop is refused while they may be the only
    /// copies. A folder video's original is missing; an Immich video has no AV1 uploaded yet and its original is gone from
    /// Immich: deleted (Immich answers 400 or 404), in the trash, which empties, or holding other bytes. Immich is asked now,
    /// and a drop it cannot answer for is refused.
    /// </summary>
    private async Task<OnlyCopyAtRisk?> OnlyCopyRefusalAsync(AppSettings settings, JobRecord job, CancellationToken cancellationToken)
    {
        var av1 = job.OutputPath is { } output && File.Exists(output) ? output : null;
        if (job.SourceKind == JobSourceKind.File)
        {
            return av1 is not null && !File.Exists(job.SourcePath) ? new OnlyCopyAtRisk(job, OnlyCopyReason.OriginalMissing) : null;
        }
        if (job.ImmichNewAssetId is not null || (av1 is null && !File.Exists(job.SourcePath))) return null;

        var immich = settings.Immich;
        if (!immich.IsConfigured) return new OnlyCopyAtRisk(job, OnlyCopyReason.ImmichNotConfigured);
        var gone = false;
        try
        {
            var asset = await immichClients.Create(immich).GetAssetAsync(job.ImmichAssetId!, cancellationToken);
            gone = asset.IsTrashed || !string.Equals(asset.Checksum, job.ImmichChecksum, StringComparison.Ordinal);
        }
        catch (ImmichApiException exception) when (exception.StatusCode is 400 or 404)
        {
            gone = true;
        }
        catch (ImmichApiException exception)
        {
            return new OnlyCopyAtRisk(job, OnlyCopyReason.ImmichUnavailable, exception.Message);
        }
        return gone ? new OnlyCopyAtRisk(job, OnlyCopyReason.ImmichOriginalGone) : null;
    }
}
