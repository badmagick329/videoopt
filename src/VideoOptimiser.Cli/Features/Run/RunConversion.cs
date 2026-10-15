using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// What <c>run</c> and <c>immich run</c> share: the banner, then the whole queue converted, folder and Immich videos alike,
/// the worker, each video in turn, and the lines after. <c>immich run</c> adds its videos to the queue first. It decides
/// flow and prints lines at once (tangle T1 in the slice plan); lifting the flow out of the CLI waits for the TUI.
/// </summary>
internal sealed class RunConversion(IQueueService queue, IJobProcessor processor, IRemoteWorkerLifecycle lifecycle, IImmichRestaging restaging, RunWorkerState state, RunStop stop, RunWorkerEnd workerEnd)
{
    /// <summary>
    /// Prints <see cref="RunBanner"/> before any download or encoding, so a config still set to <c>remoteSsh</c> is noticed
    /// before it costs anything. A retained managed worker comes from the local state file, with no Hetzner API call.
    /// </summary>
    public async Task PrintBannerAsync(LoadedConfiguration configuration, ProcessingModeOverride? modeOverride, CancellationToken cancellationToken)
    {
        var worker = lifecycle.IsManaged(configuration.Settings)
            ? await lifecycle.ReadRecordedAsync(configuration.Settings.Database.Path, cancellationToken)
            : null;
        Console.WriteLine(RunBanner.Describe(configuration.Settings, worker, modeOverride?.Flag));
    }

    /// <summary>
    /// Everything <c>run</c> does after its banner: prepares the whole queue, readies its Immich videos, and converts it,
    /// creating a managed worker only when there is work for it. The outcome is printed before the worker's fate is decided,
    /// so the worker lines come last. With nothing to convert, the run command's guard decides the fate
    /// (<see cref="RunWorkerEnd.GuardAsync"/>).
    /// </summary>
    /// <param name="beforeNext">Printed between the failed jobs and the line that says what to finalise.</param>
    public async Task<ExitCode> ConvertQueueAsync(LoadedConfiguration configuration, CliCommand command, Action? beforeNext, CancellationToken cancellationToken)
    {
        var preparation = await queue.PrepareAsync(configuration.Settings.Database.Path, configuration.Settings, sourceKind: null, modeOverridden: command.ModeOverride is not null, cancellationToken);
        var (ready, _, failures, workerUnreachable, stopped, workerHandled) = await ProcessPreparedJobsAsync(configuration, command, preparation, (ready, skipped, failures) =>
        {
            Console.WriteLine(OutcomeSummary.Run(ready, skipped, failures.Length, OutputStyle));
            PrintFailedJobs(failures);
            beforeNext?.Invoke();
            // finalize --ready finalises every source, so one line serves a queue of folder and Immich videos.
            if (ready > 0) Console.WriteLine("Next: review the outputs, then run 'video-optimiser finalize --ready'.");
        }, cancellationToken);
        return stopped ? ExitCode.Cancelled : !workerHandled ? ExitCode.GeneralFailure : QueueExitCode.Calculate(ready, failures.Length, workerUnreachable);
    }

    private async Task<(int Ready, int Skipped, FailedQueueJob[] Failures, bool WorkerUnreachable, bool Stopped, bool WorkerHandled)> ProcessPreparedJobsAsync(LoadedConfiguration configuration, CliCommand command, QueuePreparationResult preparation, Action<int, int, FailedQueueJob[]> report, CancellationToken cancellationToken)
    {
        foreach (var job in preparation.SkippedJobs) Console.WriteLine(JobCompatibility.SkippedMessage(job));
        // F8 (VOPT-70): finalize checks whether anything moved and resumes, so the run leaves these jobs alone.
        foreach (var job in preparation.InterruptedFinalizations) Console.WriteLine($"{Path.GetFileName(job.SourcePath)} was interrupted while being finalised. Run 'video-optimiser finalize --ready' to finish it.");
        if (preparation.Held > 0)
        {
            Console.WriteLine(preparation.Held == 1 ? "1 video is held; runs pass over it until it's released." : $"{preparation.Held} videos are held; runs pass over them until they're released.");
        }
        preparation = await ReadyImmichVideosAsync(configuration, preparation, cancellationToken);
        if (preparation.RunnableJobs.Count == 0)
        {
            report(0, 0, [.. preparation.FailedJobs]);
            return (0, 0, [.. preparation.FailedJobs], false, false, true);
        }

        var managed = lifecycle.IsManaged(configuration.Settings);
        // A server bills from the moment it exists, so it is created only once a job will reach a remote stage (VOPT-68).
        var managedWork = managed && await AnyJobNeedsWorkerAsync(configuration, preparation.RunnableJobs, cancellationToken);
        if (managedWork)
        {
            await EnsureWorkerReadyAsync(configuration, cancellationToken);
            state.Used = true;
        }
        await workerEnd.PrepareQuestionAsync(configuration, cancellationToken);
        Console.WriteLine($"Converting {preparation.RunnableJobs.Count} {(preparation.RunnableJobs.Count == 1 ? "video" : "videos")}.");
        var progress = new JobProgressConsole(Console.Out, OutputStyle);
        var joinFailures = new List<FailedQueueJob>();
        QueueRunResult result;
        using (workerEnd.StopAfterThisVideoAnnounced(progress))
        {
            var passOver = PassOverJoiningVideos(configuration, command, preparation, workerEnsured: managedWork, progress, joinFailures);
            result = await queue.RunAsync(configuration.Settings.Database.Path, configuration.Settings, preparation, passOver, progress, stopAfterThisVideo: stop.AfterThisVideo, cancellationToken: cancellationToken);
        }

        var failures = preparation.FailedJobs.Concat(joinFailures).Concat(result.FailedJobs).ToArray();
        if (result.UnreachableWorkerJob is not null)
        {
            Console.WriteLine();
            Console.WriteLine(UnreachableWorkerMessage.Stopped(await WorkerNameAsync(configuration.Settings), result.NotStarted + 1));
        }
        if (result.StoppedAfter is { } last)
        {
            Console.WriteLine();
            Console.WriteLine(StoppedRunMessage.StoppedAfter(last.SourcePath, result.NotStarted));
        }
        report(result.ReadyToFinalize, result.SkippedJobs.Count, failures);
        if (result.StoppedAfter is not null) Console.WriteLine(StoppedRunMessage.ConvertTheRest(command));
        var end = result.UnreachableWorkerJob is not null ? RunEnd.WorkerUnreachable : result.StoppedAfter is not null ? RunEnd.StoppedAfterVideo : RunEnd.Finished;
        var workerHandled = await workerEnd.ConcludeAsync(configuration, command, end, unreachableJob: result.UnreachableWorkerJob);
        if (result.UnreachableWorkerJob is not null)
        {
            await ReportUnreachableManualHostAsync(configuration, command, result.UnreachableWorkerJob);
        }
        else if (!managed && configuration.Settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Remote worker lifecycle is manual: after confirming all expected outputs are ready to finalise, delete the server; powering it off still bills.");
        }
        return (result.ReadyToFinalize, result.SkippedJobs.Count, failures, result.UnreachableWorkerJob is not null, result.StoppedAfter is not null, workerHandled);
    }

    /// <summary>
    /// R8: after preparation released the finalise lock and before the worker check, the Immich videos either get their
    /// downloaded copies back or stay queued with a line saying why, so the worker is created only for work that can run.
    /// Immich is asked only for a missing copy: converting needs nothing else from it. Folder videos convert either way.
    /// </summary>
    private async Task<QueuePreparationResult> ReadyImmichVideosAsync(LoadedConfiguration configuration, QueuePreparationResult preparation, CancellationToken cancellationToken)
    {
        var immichJobs = preparation.RunnableJobs.Where(job => job.SourceKind == JobSourceKind.Immich).ToArray();
        var foldersOnly = preparation with { RunnableJobs = preparation.RunnableJobs.Where(job => job.SourceKind == JobSourceKind.File).ToArray(), SourceKind = JobSourceKind.File };
        // Without Immich the run takes no Immich video, a joining one included.
        if (!configuration.Settings.Immich.IsConfigured)
        {
            if (immichJobs.Length > 0) Console.WriteLine(ImmichStayQueued(immichJobs.Length, "immich.baseUrl isn't set"));
            return foldersOnly;
        }
        if (immichJobs.Length == 0) return preparation;

        var restage = await RestageAsync(configuration, immichJobs, cancellationToken);
        var failed = restage.Failed.Select(job => job.Id).ToHashSet();
        var ready = restage.Unusable is null ? preparation : foldersOnly;
        return ready with
        {
            RunnableJobs = ready.RunnableJobs.Where(job => !failed.Contains(job.Id)).ToArray(),
            FailedJobs = [.. preparation.FailedJobs, .. restage.Failed.Select(job => ToFailedQueueJob(job, job.FailureMessage!))]
        };
    }

    /// <summary>
    /// Downloads again the missing copies of <paramref name="jobs"/>, printing today's restaging lines. When Immich can't be
    /// used, says how many of them stay queued: those it did not fail before it stopped answering.
    /// </summary>
    public async Task<ImmichRestageResult> RestageAsync(LoadedConfiguration configuration, JobRecord[] jobs, CancellationToken cancellationToken)
    {
        var progress = new ImmichDiscoveryConsole(Console.Out, OutputStyle, Console.Error, ErrorStyle);
        var restage = await restaging.RestageMissingAsync(configuration.Settings.Database.Path, configuration.Settings, jobs, progress, cancellationToken);
        if (restage.Unusable is { } reason)
        {
            progress.FinishLine();
            Console.WriteLine(ImmichStayQueued(jobs.Length - restage.Failed.Count, $"Immich can't be used ({reason})"));
        }
        return restage;
    }

    public static string ImmichStayQueued(int count, string reason) =>
        count == 1 ? $"1 Immich video stays queued: {reason}." : $"{count} Immich videos stay queued: {reason}.";

    public static FailedQueueJob ToFailedQueueJob(JobRecord job, string message) => new(job.Id, job.SourcePath, job.FailureCategory ?? "ProcessingFailed", job.FailureMessage ?? message);

    public static void PrintFailedJobs(FailedQueueJob[] failedJobs)
    {
        if (failedJobs.Length == 0) return;
        Console.WriteLine();
        Console.WriteLine(OutputStyle.Red("Failed jobs:"));
        foreach (var job in failedJobs)
        {
            Console.WriteLine($"{job.Id:N}  {Path.GetFileName(job.SourcePath)}");
            Console.WriteLine($"{job.FailureCategory}: {job.FailureMessage}");
        }
    }

    /// <summary>In run order, stopping at the first job that needs the worker: usually only the first job is probed twice.</summary>
    private async Task<bool> AnyJobNeedsWorkerAsync(LoadedConfiguration configuration, IReadOnlyList<JobRecord> jobs, CancellationToken cancellationToken)
    {
        foreach (var job in jobs)
        {
            if (await processor.NeedsWorkerAsync(job, configuration.Settings, cancellationToken)) return true;
        }
        return false;
    }

    /// <summary>
    /// For a video that joins the run after it prepared (queued, released or moved next from another terminal), the checks
    /// preparation made for the rest. Under <c>--local</c> or <c>--remote</c>, one started in the other mode is skipped, as
    /// preparation skips it. A managed run that created no worker cannot start one that needs it: it would stop at its
    /// first remote stage with nothing to connect to, so it waits for the next run. An Immich video whose downloaded copy is
    /// gone gets it back first, as preparation's do: one Immich no longer has fails into <paramref name="failures"/>, and one
    /// Immich can't be asked about stays queued.
    /// </summary>
    private Func<JobRecord, CancellationToken, Task<bool>> PassOverJoiningVideos(LoadedConfiguration configuration, CliCommand command, QueuePreparationResult preparation, bool workerEnsured, JobProgressConsole progress, List<FailedQueueJob> failures)
    {
        var prepared = preparation.RunnableJobs.Select(job => job.Id).ToHashSet();
        var needsCheck = !workerEnsured && lifecycle.IsManaged(configuration.Settings);
        return async (job, cancellationToken) =>
        {
            if (prepared.Contains(job.Id)) return false;
            if (command.ModeOverride is not null && JobCompatibility.IsBoundToOtherMode(job, JobCompatibility.Evaluate(job, configuration.Settings)))
            {
                progress.WriteNote(JobCompatibility.SkippedMessage(job));
                return true;
            }
            if (job.SourceKind == JobSourceKind.Immich)
            {
                var restage = await RestageAsync(configuration, [job], cancellationToken);
                failures.AddRange(restage.Failed.Select(failed => ToFailedQueueJob(failed, failed.FailureMessage!)));
                if (restage.Failed.Count > 0 || restage.Unusable is not null) return true;
            }
            if (!needsCheck || !await processor.NeedsWorkerAsync(job, configuration.Settings, cancellationToken)) return false;
            progress.WriteNote($"{Path.GetFileName(job.SourcePath)} waits for the next run: it needs a worker, and this run has none.");
            return true;
        };
    }

    /// <summary>U3m (VOPT-69): a manual host that stopped answering is neither billed nor deleted by this tool, so the user hears only how to resume.</summary>
    private async Task ReportUnreachableManualHostAsync(LoadedConfiguration configuration, CliCommand command, JobRecord unreachableWorkerJob)
    {
        if (lifecycle.IsManaged(configuration.Settings)) return;
        Console.WriteLine();
        foreach (var line in UnreachableWorkerMessage.Lines(unreachableWorkerJob, managed: false, command)) Console.WriteLine(line);
    }

    /// <summary>The managed server's name, or the SSH host of a manual one. Read without the run's token, which may be cancelled.</summary>
    private async Task<string> WorkerNameAsync(AppSettings settings)
    {
        // Not the settings host for a managed worker: connecting replaced it with the server's IP address.
        var managedName = lifecycle.IsManaged(settings) ? (await lifecycle.ReadRecordedAsync(settings.Database.Path, CancellationToken.None))?.Name : null;
        return managedName ?? settings.Processing.RemoteSsh.Host;
    }

    private Task<RemoteWorkerInfo> EnsureWorkerReadyAsync(LoadedConfiguration configuration, CancellationToken cancellationToken) =>
        lifecycle.EnsureReadyAsync(configuration.Settings, configuration.Settings.Database.Path, new InlineProgress<string>(Console.WriteLine), ConsolePrompts.ConfirmServerCreationAsync, cancellationToken);
}
