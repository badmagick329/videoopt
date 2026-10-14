using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Jobs;

/// <summary>
/// The queue verbs (VOPT-54 step 5): <c>queue hold</c>, <c>release</c>, <c>next</c> and <c>drop</c>. Each writes only while the
/// job is as it read it (the repository's compare-and-swap), so a run that started the video first wins and a verb never
/// half-applies. A job in a converting status is converting only while a run holds the run lock; otherwise a crash or a
/// closed window left it, and <see cref="RunHoldsJobsAsync"/> marks it stopped first. Drop never leaves <c>cleanup</c> the
/// only copy of a video. The wording was approved by the user word for word; ask before changing it.
/// </summary>
internal static partial class CliApplication
{
    private static async Task<int> RunQueueHoldAsync(IJobRepository repository, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var databasePath = configuration.Settings.Database.Path;
        if (await FindJobAsync(repository, databasePath, command, cancellationToken) is not { } job) return (int)ExitCode.InvalidArguments;
        var name = Path.GetFileName(job.SourcePath);
        if (IsConverting(job.Status))
        {
            if (await RunHoldsJobsAsync(repository, databasePath, "queue hold", cancellationToken)) return Refuse($"{name} is converting now ({StatusWord(job.Status)}). To hold it, press Ctrl+C twice in the run's window first.");
            job = await ReadAgainAsync(repository, databasePath, job, cancellationToken);
        }
        if (job.Held)
        {
            Console.WriteLine($"{name} is already held.");
            return (int)ExitCode.Success;
        }
        if (!job.IsQueued) return Refuse(NotQueuedRefusal(job, "be held", databasePath, command));
        if (!await repository.SetHeldAsync(databasePath, job, held: true, cancellationToken)) throw new JobChangedException(job.Id);
        Console.WriteLine($"Held {name}. Runs pass over it until '{command.Hint($"queue release {job.Id:N}")}'.");
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunQueueReleaseAsync(IJobRepository repository, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var databasePath = configuration.Settings.Database.Path;
        if (await FindJobAsync(repository, databasePath, command, cancellationToken) is not { } job) return (int)ExitCode.InvalidArguments;
        var name = Path.GetFileName(job.SourcePath);
        // Only a waiting or stopped job is ever held, and dropping one releases it, so a held job is always one to release.
        if (!job.Held)
        {
            Console.WriteLine($"{name} isn't held.");
            return (int)ExitCode.Success;
        }
        if (!await repository.SetHeldAsync(databasePath, job, held: false, cancellationToken)) throw new JobChangedException(job.Id);
        Console.WriteLine($"Released {name}; runs take it again.");
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunQueueNextAsync(IJobRepository repository, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var databasePath = configuration.Settings.Database.Path;
        if (await FindJobAsync(repository, databasePath, command, cancellationToken) is not { } job) return (int)ExitCode.InvalidArguments;
        var name = Path.GetFileName(job.SourcePath);
        if (IsConverting(job.Status))
        {
            if (await RunHoldsJobsAsync(repository, databasePath, "queue next", cancellationToken))
            {
                Console.WriteLine($"{name} is converting now.");
                return (int)ExitCode.Success;
            }
            job = await ReadAgainAsync(repository, databasePath, job, cancellationToken);
        }
        if (!job.IsQueued) return Refuse(NotQueuedRefusal(job, "run next", databasePath, command));
        var wasHeld = job.Held;
        // A run takes each next video from the queue as it is, so this one comes after the video converting now. An immich
        // run keeps the videos it started with (until phase A step 7), as does a run from an older version.
        var runTakesIt = LiveRun(databasePath) is null or RunHolder;
        if (!await repository.RunNextAsync(databasePath, job, cancellationToken)) throw new JobChangedException(job.Id);
        Console.WriteLine($"{name} {(runTakesIt ? "runs next" : "is now first in the queue")}{(wasHeld ? "; it's no longer held" : string.Empty)}.");
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunQueueDropAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (command.All) return await DropAllAsync(services, configuration, command, cancellationToken);
        var repository = services.GetRequiredService<IJobRepository>();
        var databasePath = configuration.Settings.Database.Path;
        if (await FindJobAsync(repository, databasePath, command, cancellationToken) is not { } job) return (int)ExitCode.InvalidArguments;
        var name = Path.GetFileName(job.SourcePath);
        if (IsConverting(job.Status))
        {
            if (await RunHoldsJobsAsync(repository, databasePath, "queue drop", cancellationToken)) return Refuse($"{name} is converting now ({StatusWord(job.Status)}). To drop it, press Ctrl+C twice in the run's window first.");
            job = await ReadAgainAsync(repository, databasePath, job, cancellationToken);
        }
        // Finalize resumes these, and dropping one would leave cleanup a half-installed AV1 to delete.
        if (FinalisationRefusal(job, databasePath, command) is { } finalising) return Refuse(finalising);
        if (job.IsTerminal)
        {
            WriteError($"{name} {StatusPhrase(job)}, so there is nothing to drop.");
            if (CleanupDeletesSomeOf(job, await CleanupPlanAsync(services, configuration, cancellationToken))) WriteError($"'{command.Hint("cleanup")}' deletes its temporary files.");
            return (int)ExitCode.GeneralFailure;
        }

        var finished = HasFinishedAv1(job);
        // Under the finalise lock, so a finalisation cannot start installing the AV1 this drop hands to cleanup. One that ran
        // while this waited changed the job, and the drop's compare-and-swap refuses it.
        using var finalizeLock = finished ? await TakeFinalizeLockAsync(configuration, "queue drop", cancellationToken, $"A finalise is running on this database. Wait for it to finish, then drop {name}.") : null;
        if (finished)
        {
            if (finalizeLock is null) return (int)ExitCode.GeneralFailure;
            if (await OnlyCopyRefusalAsync(services, configuration, job, cancellationToken) is { } onlyCopy) return Refuse(onlyCopy);
        }
        if (!await repository.DropAsync(databasePath, job, cancellationToken)) throw new JobChangedException(job.Id);
        Console.WriteLine($"Dropped {name}.");
        if (RemoteStageLine(job) is { } remote) Console.WriteLine(remote);
        if (LeftFilesLine(job, finished, await CleanupPlanAsync(services, configuration, cancellationToken), command) is { } files) Console.WriteLine(files);
        if (job.ImmichNewAssetId is { } uploaded) Console.WriteLine($"The AV1 already uploaded to Immich as asset {uploaded} stays there; delete it in Immich if you do not want it.");
        return (int)ExitCode.Success;
    }

    /// <summary>
    /// Drops every waiting, stopped and held video. A finished AV1 is dropped by ID only, so a stopped check is left with
    /// the command for it, as is a stopped finalisation, which finalize resumes.
    /// </summary>
    private static async Task<int> DropAllAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var repository = services.GetRequiredService<IJobRepository>();
        var databasePath = configuration.Settings.Database.Path;
        var open = await repository.ListAsync(databasePath, terminal: false, cancellationToken);
        if (open.Any(job => IsConverting(job.Status)) && !await RunHoldsJobsAsync(repository, databasePath, "queue drop", cancellationToken))
        {
            open = await repository.ListAsync(databasePath, terminal: false, cancellationToken);
        }
        var finalising = FinaliseGoing(databasePath);
        var drop = JobRunOrder.Sort(open.Where(job => job.IsQueued && !HasFinishedAv1(job))).ToArray();
        var stoppedFinalising = open.Where(job => job is { Status: JobStatus.Interrupted, ResumeStatus: JobStatus.Finalizing } || job.Status == JobStatus.Finalizing && !finalising).ToArray();
        var stoppedChecking = open.Where(job => job is { Status: JobStatus.Interrupted, ResumeStatus: JobStatus.Validating }).ToArray();
        if (drop.Length == 0 && stoppedFinalising.Length == 0 && stoppedChecking.Length == 0)
        {
            Console.WriteLine("Nothing to drop: the queue is empty.");
            return (int)ExitCode.Success;
        }

        var exitCode = ExitCode.Success;
        foreach (var job in drop)
        {
            if (!await repository.DropAsync(databasePath, job, cancellationToken))
            {
                // A run started it, or a verb from another terminal changed it, since the list was read. The rest still go.
                WriteError(new JobChangedException(job.Id).Message);
                exitCode = ExitCode.GeneralFailure;
                continue;
            }
            Console.WriteLine($"Dropped {Path.GetFileName(job.SourcePath)}.");
            if (RemoteStageLine(job) is { } remote) Console.WriteLine(remote);
        }
        foreach (var job in stoppedFinalising) Console.WriteLine($"Left 1 video stopped while being finalised; '{command.Hint($"finalize {job.Id:N}")}' checks and resumes it.");
        foreach (var job in stoppedChecking) Console.WriteLine($"Left 1 video stopped while its AV1 was checked; to drop it: {command.Hint($"queue drop {job.Id:N}")}");
        return (int)exitCode;
    }

    /// <summary>The job the command names, or null after saying why there is none (exit 2, as for <c>queue retry</c>).</summary>
    private static async Task<JobRecord?> FindJobAsync(IJobRepository repository, string databasePath, CliCommand command, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(command.JobId, out var id))
        {
            WriteError($"'{command.JobId}' is not a job ID. '{command.Hint("queue list")}' shows them.");
            return null;
        }
        var job = await repository.GetAsync(databasePath, id, cancellationToken);
        if (job is null) WriteError($"No job has ID {id:N}.");
        return job;
    }

    private static async Task<JobRecord> ReadAgainAsync(IJobRepository repository, string databasePath, JobRecord job, CancellationToken cancellationToken) =>
        await repository.GetAsync(databasePath, job.Id, cancellationToken) ?? throw new JobChangedException(job.Id);

    /// <summary>
    /// R9: whether a run holds the run lock, so its converting jobs are really converting. When none does, a crash or a
    /// closed window left them, and they are marked stopped as a run's preparation marks them, under the same finalise-lock
    /// guard. The run lock is taken directly, not through <see cref="TakeRunLock"/>, which would start deciding the worker's
    /// fate, and only for the moment this takes.
    /// </summary>
    private static async Task<bool> RunHoldsJobsAsync(IJobRepository repository, string databasePath, string verb, CancellationToken cancellationToken)
    {
        using var runLock = DatabaseLock.TryAcquire(DatabaseLock.RunPath(databasePath), verb, out _);
        if (runLock is null) return true;
        using var finalizeLock = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(databasePath), DatabaseLock.RunPreparing, out _);
        await repository.MarkActiveJobsInterruptedAsync(databasePath, includeFinalizing: finalizeLock is not null, cancellationToken);
        return false;
    }

    private static bool FinaliseGoing(string databasePath) =>
        DatabaseLock.ReadLiveHolder(DatabaseLock.FinalizePath(databasePath)) is { } holder && holder.Command != DatabaseLock.RunPreparing;

    private static bool IsConverting(JobStatus status) =>
        status is JobStatus.Staging or JobStatus.CrfSearching or JobStatus.Encoding or JobStatus.Downloading or JobStatus.Validating;

    /// <summary>Ready, or stopped while its AV1 was checked: the AV1 is whole, and cleanup would delete it once the job ends.</summary>
    private static bool HasFinishedAv1(JobRecord job) => job.Status == JobStatus.ReadyToFinalize || job is { Status: JobStatus.Interrupted, ResumeStatus: JobStatus.Validating };

    private static int Refuse(string message)
    {
        WriteError(message);
        return (int)ExitCode.GeneralFailure;
    }

    /// <summary>A finalisation, stopped or going, belongs to finalize; null for any other job.</summary>
    private static string? FinalisationRefusal(JobRecord job, string databasePath, CliCommand command)
    {
        var name = Path.GetFileName(job.SourcePath);
        var stopped = $"{name} was stopped while being finalised; '{command.Hint($"finalize {job.Id:N}")}' checks and resumes it.";
        return job switch
        {
            { Status: JobStatus.Interrupted, ResumeStatus: JobStatus.Finalizing } => stopped,
            { Status: JobStatus.Finalizing } => FinaliseGoing(databasePath) ? $"{name} is being finalised now." : stopped,
            _ => null
        };
    }

    /// <param name="action">How the verb ends the sentence "Only waiting and stopped videos can …", such as <c>be held</c>.</param>
    private static string NotQueuedRefusal(JobRecord job, string action, string databasePath, CliCommand command) =>
        FinalisationRefusal(job, databasePath, command) ?? $"Only waiting and stopped videos can {action}; {Path.GetFileName(job.SourcePath)} {StatusPhrase(job)}.";

    private static string StatusPhrase(JobRecord job) => job.Status switch
    {
        JobStatus.ReadyToFinalize => "is ready to finalise",
        JobStatus.Failed => "has already failed",
        JobStatus.Cancelled => "was already dropped",
        JobStatus.Completed => "was already finalised",
        JobStatus.Skipped => "was not worth converting",
        _ => $"is {StatusWord(job.Status)}"
    };

    /// <summary>The glossary's word for a status, lower case for use inside a sentence.</summary>
    private static string StatusWord(JobStatus status) => status switch
    {
        JobStatus.Queued => "waiting",
        JobStatus.Staging => "uploading",
        JobStatus.CrfSearching => "finding CRF",
        JobStatus.Encoding => "encoding",
        JobStatus.Downloading => "downloading",
        JobStatus.Validating => "checking",
        JobStatus.Finalizing => "finalising",
        JobStatus.Interrupted => "stopped",
        _ => status.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// W3: a remote CRF search or encode runs detached, so a stop leaves it going, and dropping the job does not stop it
    /// (<c>VOPT-90</c>). A managed worker's deletion ends it; a manual worker's runs until it finishes.
    /// </summary>
    private static string? RemoteStageLine(JobRecord job)
    {
        if (!string.Equals(job.ExecutionMode, ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase) || job.ResumeStatus is not (JobStatus.CrfSearching or JobStatus.Encoding)) return null;
        var stage = job.ResumeStatus == JobStatus.Encoding ? "encode" : "CRF search";
        return RemoteExecutionIdentity.IsManagedHetzner(job.RemoteHost)
            ? $"Its {stage} may still be running on the worker; deleting the worker stops it."
            : $"Its {stage} may still be running on the worker until it finishes.";
    }

    private static Task<TemporaryOutputCleanupPlan> CleanupPlanAsync(IServiceProvider services, LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        var settings = configuration.Settings;
        return services.GetRequiredService<ITemporaryOutputCleanupService>().FindAsync(settings.Database.Path, settings.Immich.IsConfigured ? settings.Immich.StagingDirectory : string.Empty, cancellationToken);
    }

    private static bool CleanupDeletesSomeOf(JobRecord job, TemporaryOutputCleanupPlan plan) =>
        plan.Abandoned.Any(file => file.JobId == job.Id) || plan.StagedOriginals.Any(file => file.JobId == job.Id);

    /// <summary>What of a dropped job cleanup will delete, read from cleanup's own plan so the line cannot promise more.</summary>
    /// <param name="finished">Whether the job had a finished AV1 before the drop; any other AV1 file of it is unfinished.</param>
    private static string? LeftFilesLine(JobRecord job, bool finished, TemporaryOutputCleanupPlan plan, CliCommand command)
    {
        var av1Files = plan.Abandoned.Where(file => file.JobId == job.Id && !file.Path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        var finishedAv1 = finished && job.OutputPath is { } output && av1Files.Any(file => string.Equals(Path.GetFullPath(file.Path), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase));
        var av1 = av1Files.Length == 0 ? null : finishedAv1 ? "AV1" : "unfinished AV1";
        var copy = plan.StagedOriginals.Any(file => file.JobId == job.Id);
        var cleanup = command.Hint("cleanup");
        return (av1, copy) switch
        {
            (null, false) => null,
            (null, true) => $"Its downloaded copy stays until '{cleanup}' deletes it.",
            (_, false) => $"Its {av1} stays until '{cleanup}' deletes it.",
            _ => $"Its {av1} and downloaded copy stay until '{cleanup}' deletes them."
        };
    }

    /// <summary>
    /// R5 (W1, W2): cleanup deletes a dropped job's AV1 and downloaded copy, so a drop is refused while they may be the only
    /// copies. A folder video's original is missing; an Immich video has no AV1 uploaded yet and its original is gone from
    /// Immich: deleted (Immich answers 400 or 404), in the trash, which empties, or holding other bytes. Immich is asked now,
    /// and a drop it cannot answer for is refused.
    /// </summary>
    private static async Task<string?> OnlyCopyRefusalAsync(IServiceProvider services, LoadedConfiguration configuration, JobRecord job, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(job.SourcePath);
        var av1 = job.OutputPath is { } output && File.Exists(output) ? output : null;
        if (job.SourceKind == JobSourceKind.File)
        {
            return av1 is not null && !File.Exists(job.SourcePath)
                ? $"The original of {name} is missing, so its AV1 at {av1} may be the only copy. Put the original back, or move the AV1 somewhere safe, then drop the video."
                : null;
        }
        if (job.ImmichNewAssetId is not null || (av1 is null && !File.Exists(job.SourcePath))) return null;

        var immich = configuration.Settings.Immich;
        string? unusable = null;
        var gone = false;
        if (!immich.IsConfigured)
        {
            unusable = "immich.baseUrl isn't set";
        }
        else
        {
            try
            {
                var asset = await services.GetRequiredService<IImmichClientFactory>().Create(immich).GetAssetAsync(job.ImmichAssetId!, cancellationToken);
                gone = asset.IsTrashed || !string.Equals(asset.Checksum, job.ImmichChecksum, StringComparison.Ordinal);
            }
            catch (ImmichApiException exception) when (exception.StatusCode is 400 or 404)
            {
                gone = true;
            }
            catch (ImmichApiException exception)
            {
                unusable = exception.Message.TrimEnd('.');
            }
        }
        if (unusable is not null) return $"Immich can't be used ({unusable}), so video-optimiser can't check that the original of {name} is still there. Try again once it answers.";
        return gone ? $"The original of {name} is gone from Immich, so its AV1 and downloaded copy may be the only copies: {job.OutputPath}, {job.SourcePath}. Move them somewhere safe, then drop the video." : null;
    }
}
