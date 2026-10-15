using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Cli;
using VideoOptimiser.Cli.Features.Calibrate;
using VideoOptimiser.Cli.Features.Cleanup;
using VideoOptimiser.Cli.Features.Config;
using VideoOptimiser.Cli.Features.Doctor;
using VideoOptimiser.Cli.Features.Finalize;
using VideoOptimiser.Cli.Features.Queue;
using VideoOptimiser.Cli.Features.RemoteSetup;
using VideoOptimiser.Cli.Features.Validate;
using VideoOptimiser.Cli.Features.Worker;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using static CliOutput;

NativeSqliteBootstrapper.Initialize();
return await CliApplication.RunAsync(args);

internal static partial class CliApplication
{
    /// <param name="configureServices">Replaces services after the real ones are registered; tests use it to put fakes behind the real CLI.</param>
    public static async Task<int> RunAsync(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        try
        {
            var command = CliCommand.Parse(args);
            OutputStyle = TerminalStyle.ForOutput(command.Json);
            ErrorStyle = TerminalStyle.ForError(command.Json);
            if (command.ShowHelp)
            {
                Console.WriteLine(CliCommand.Usage);
                return (int)ExitCode.Success;
            }

            if (command.Kind == CliCommandKind.Version)
            {
                Console.WriteLine($"video-optimiser {GetVersion()}");
                return (int)ExitCode.Success;
            }

            if (!command.IsValid)
            {
                WriteError(command.Error);
                Console.Error.WriteLine(CliCommand.Usage);
                return (int)ExitCode.InvalidArguments;
            }

            using var host = BuildHost(configureServices);
            var stop = host.Services.GetRequiredService<RunStop>();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stop.Press();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                return await ExecuteAsync(host.Services, command, stop.Now);
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
        catch (OperationCanceledException)
        {
            return (int)ExitCode.Cancelled;
        }
        catch (Exception exception)
        {
            return ReportFailure(exception);
        }
    }

    private static IHost BuildHost(Action<IServiceCollection>? configureServices)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddVideoOptimiser();
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }

    private static async Task<int> ExecuteAsync(IServiceProvider services, CliCommand command, CancellationToken cancellationToken)
    {
        if (command.Kind == CliCommandKind.ConfigInit) return await services.GetRequiredService<ConfigCommands>().InitAsync(command, cancellationToken);

        var configurationLoader = services.GetRequiredService<IConfigurationLoader>();
        var configuration = await configurationLoader.LoadAsync(command.ConfigurationPath, cancellationToken);
        command.ModeOverride?.Apply(configuration.Settings);
        if (command.Kind is CliCommandKind.WorkerCreate or CliCommandKind.WorkerStatus or CliCommandKind.WorkerDelete
            && WorkerCommands.RequireManagedLifecycle(command.Kind, configuration) is { } refused)
        {
            return refused;
        }
        return command.Kind switch
        {
            CliCommandKind.ConfigShow => ConfigCommands.Show(configuration),
            CliCommandKind.ConfigValidate => services.GetRequiredService<ConfigCommands>().Validate(configuration),
            CliCommandKind.Doctor => await services.GetRequiredService<DoctorCommand>().RunAsync(configuration, command.Json, cancellationToken),
            CliCommandKind.WorkerCreate => await services.GetRequiredService<WorkerCommands>().CreateAsync(configuration, cancellationToken),
            CliCommandKind.WorkerStatus => await services.GetRequiredService<WorkerCommands>().StatusAsync(configuration, cancellationToken),
            CliCommandKind.WorkerDelete => await services.GetRequiredService<WorkerCommands>().DeleteAsync(configuration, cancellationToken),
            CliCommandKind.RemoteSetup => await services.GetRequiredService<RemoteSetupCommand>().RunAsync(configuration, cancellationToken),
            CliCommandKind.QueueAdd => await services.GetRequiredService<QueueCommands>().AddAsync(configuration, command, cancellationToken),
            CliCommandKind.QueueAddFile => await services.GetRequiredService<QueueCommands>().AddFileAsync(configuration, command, cancellationToken),
            CliCommandKind.Run => await RunGuardedAsync(services, configuration, command, () => RunRunAsync(services, configuration, command, cancellationToken)),
            CliCommandKind.QueueList => await services.GetRequiredService<QueueCommands>().ListAsync(configuration, terminal: false, command.Json, cancellationToken),
            CliCommandKind.QueueHold => await services.GetRequiredService<QueueCommands>().HoldAsync(configuration, command, cancellationToken),
            CliCommandKind.QueueRelease => await services.GetRequiredService<QueueCommands>().ReleaseAsync(configuration, command, cancellationToken),
            CliCommandKind.QueueNext => await services.GetRequiredService<QueueCommands>().NextAsync(configuration, command, cancellationToken),
            CliCommandKind.QueueDrop => await services.GetRequiredService<QueueCommands>().DropAsync(configuration, command, cancellationToken),
            CliCommandKind.QueueRetry => await services.GetRequiredService<QueueCommands>().RetryAsync(configuration, command, cancellationToken),
            CliCommandKind.Status => await services.GetRequiredService<QueueCommands>().ListAsync(configuration, terminal: false, command.Json, cancellationToken),
            CliCommandKind.History => await services.GetRequiredService<QueueCommands>().ListAsync(configuration, terminal: true, command.Json, cancellationToken),
            CliCommandKind.Validate => await services.GetRequiredService<ValidateCommand>().RunAsync(configuration, command.JobId!, cancellationToken),
            CliCommandKind.Finalize => await services.GetRequiredService<FinalizeCommands>().FinalizeAsync(configuration, command, cancellationToken),
            CliCommandKind.Cleanup => await services.GetRequiredService<CleanupCommand>().RunAsync(configuration, command.DryRun, cancellationToken),
            CliCommandKind.ImmichRun => await RunGuardedAsync(services, configuration, command, () => RunImmichRunAsync(services, configuration, command, cancellationToken)),
            CliCommandKind.ImmichFinalize => await services.GetRequiredService<FinalizeCommands>().ImmichFinalizeAsync(configuration, cancellationToken),
            CliCommandKind.Calibrate => await services.GetRequiredService<CalibrateCommand>().RunAsync(configuration, command.Json, cancellationToken),
            _ => (int)ExitCode.InvalidArguments
        };
    }

    /// <summary>
    /// Prints <see cref="RunBanner"/> before any download or encoding, so a config still set to <c>remoteSsh</c> is noticed
    /// before it costs anything. A retained managed worker comes from the local state file, with no Hetzner API call.
    /// </summary>
    private static async Task PrintRunBannerAsync(IServiceProvider services, LoadedConfiguration configuration, ProcessingModeOverride? modeOverride, CancellationToken cancellationToken)
    {
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        var worker = lifecycle.IsManaged(configuration.Settings)
            ? await lifecycle.ReadRecordedAsync(configuration.Settings.Database.Path, cancellationToken)
            : null;
        Console.WriteLine(RunBanner.Describe(configuration.Settings, worker, modeOverride?.Flag));
    }

    /// <summary>
    /// Processes prepared jobs, creating a managed worker only when there is work for it. <paramref name="report"/> prints the
    /// outcome before the worker's fate is decided, so the worker lines come last. With nothing to convert, the run command's
    /// guard decides the fate (<see cref="RunGuardedAsync"/>).
    /// </summary>
    /// <param name="beforeWorker">Runs after the preparation's lines and before the worker check, and returns the preparation
    /// to convert: <c>run</c> readies its Immich videos there (R8).</param>
    private static async Task<(int Ready, int Skipped, FailedQueueJob[] Failures, bool WorkerUnreachable, bool Stopped, bool WorkerHandled)> ProcessPreparedJobsAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, QueuePreparationResult preparation, Action<int, int, FailedQueueJob[]> report, CancellationToken cancellationToken, Func<QueuePreparationResult, Task<QueuePreparationResult>>? beforeWorker = null)
    {
        foreach (var job in preparation.SkippedJobs) Console.WriteLine(JobCompatibility.SkippedMessage(job));
        // F8 (VOPT-70): finalize checks whether anything moved and resumes, so the run leaves these jobs alone.
        foreach (var job in preparation.InterruptedFinalizations) Console.WriteLine($"{Path.GetFileName(job.SourcePath)} was interrupted while being finalised. Run 'video-optimiser finalize --ready' to finish it.");
        if (command.Kind == CliCommandKind.Run && preparation.Held > 0)
        {
            Console.WriteLine(preparation.Held == 1 ? "1 video is held; runs pass over it until it's released." : $"{preparation.Held} videos are held; runs pass over them until they're released.");
        }
        if (beforeWorker is not null) preparation = await beforeWorker(preparation);
        if (preparation.RunnableJobs.Count == 0)
        {
            report(0, 0, [.. preparation.FailedJobs]);
            return (0, 0, [.. preparation.FailedJobs], false, false, true);
        }

        var queue = services.GetRequiredService<IQueueService>();
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        var managed = lifecycle.IsManaged(configuration.Settings);
        // A server bills from the moment it exists, so it is created only once a job will reach a remote stage (VOPT-68).
        var managedWork = managed && await AnyJobNeedsWorkerAsync(services, configuration, preparation.RunnableJobs, cancellationToken);
        if (managedWork)
        {
            await EnsureWorkerReadyAsync(services, configuration, cancellationToken);
            services.GetRequiredService<RunWorkerState>().Used = true;
        }
        await PrepareWorkerQuestionAsync(services, configuration, cancellationToken);
        Console.WriteLine($"Converting {preparation.RunnableJobs.Count} {(preparation.RunnableJobs.Count == 1 ? "video" : "videos")}.");
        var progress = new JobProgressConsole(Console.Out, OutputStyle);
        var joinFailures = new List<FailedQueueJob>();
        QueueRunResult result;
        using (StopAfterThisVideoAnnounced(services, progress))
        {
            var passOver = PassOverJoiningVideos(services, configuration, command, preparation, workerEnsured: managedWork, progress, joinFailures);
            result = await queue.RunAsync(configuration.Settings.Database.Path, configuration.Settings, preparation, passOver, progress, stopAfterThisVideo: services.GetRequiredService<RunStop>().AfterThisVideo, cancellationToken: cancellationToken);
        }

        var failures = preparation.FailedJobs.Concat(joinFailures).Concat(result.FailedJobs).ToArray();
        if (result.UnreachableWorkerJob is not null)
        {
            Console.WriteLine();
            Console.WriteLine(UnreachableWorkerMessage.Stopped(await WorkerNameAsync(services, configuration.Settings), result.NotStarted + 1));
        }
        if (result.StoppedAfter is { } last)
        {
            Console.WriteLine();
            Console.WriteLine(StoppedRunMessage.StoppedAfter(last.SourcePath, result.NotStarted));
        }
        report(result.ReadyToFinalize, result.SkippedJobs.Count, failures);
        if (result.StoppedAfter is not null) Console.WriteLine(StoppedRunMessage.ConvertTheRest(command, await ImmichJobsLeftAsync(services, configuration, command)));
        var end = result.UnreachableWorkerJob is not null ? RunEnd.WorkerUnreachable : result.StoppedAfter is not null ? RunEnd.StoppedAfterVideo : RunEnd.Finished;
        var workerHandled = await ConcludeWorkerAsync(services, configuration, command, end, unreachableJob: result.UnreachableWorkerJob);
        if (result.UnreachableWorkerJob is not null)
        {
            await ReportUnreachableManualHostAsync(services, configuration, command, result.UnreachableWorkerJob);
        }
        else if (!managed && configuration.Settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Remote worker lifecycle is manual: after confirming all expected outputs are ready to finalise, delete the server; powering it off still bills.");
        }
        return (result.ReadyToFinalize, result.SkippedJobs.Count, failures, result.UnreachableWorkerJob is not null, result.StoppedAfter is not null, workerHandled);
    }

    /// <summary>In run order, stopping at the first job that needs the worker: usually only the first job is probed twice.</summary>
    private static async Task<bool> AnyJobNeedsWorkerAsync(IServiceProvider services, LoadedConfiguration configuration, IReadOnlyList<JobRecord> jobs, CancellationToken cancellationToken)
    {
        var processor = services.GetRequiredService<IJobProcessor>();
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
    private static Func<JobRecord, CancellationToken, Task<bool>> PassOverJoiningVideos(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, QueuePreparationResult preparation, bool workerEnsured, JobProgressConsole progress, List<FailedQueueJob> failures)
    {
        var prepared = preparation.RunnableJobs.Select(job => job.Id).ToHashSet();
        var needsCheck = !workerEnsured && services.GetRequiredService<IRemoteWorkerLifecycle>().IsManaged(configuration.Settings);
        var processor = services.GetRequiredService<IJobProcessor>();
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
                var restage = await RestageAsync(services, configuration, [job], cancellationToken);
                failures.AddRange(restage.Failed.Select(failed => ToFailedQueueJob(failed, failed.FailureMessage!)));
                if (restage.Failed.Count > 0 || restage.Unusable is not null) return true;
            }
            if (!needsCheck || !await processor.NeedsWorkerAsync(job, configuration.Settings, cancellationToken)) return false;
            progress.WriteNote($"{Path.GetFileName(job.SourcePath)} waits for the next run: it needs a worker, and this run has none.");
            return true;
        };
    }

    /// <summary>U3m (VOPT-69): a manual host that stopped answering is neither billed nor deleted by this tool, so the user hears only how to resume.</summary>
    private static async Task ReportUnreachableManualHostAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, JobRecord unreachableWorkerJob)
    {
        if (services.GetRequiredService<IRemoteWorkerLifecycle>().IsManaged(configuration.Settings)) return;
        Console.WriteLine();
        foreach (var line in UnreachableWorkerMessage.Lines(unreachableWorkerJob, managed: false, command, await ImmichJobsLeftAsync(services, configuration, command))) Console.WriteLine(line);
    }

    /// <summary>The managed server's name, or the SSH host of a manual one. Read without the run's token, which may be cancelled.</summary>
    private static async Task<string> WorkerNameAsync(IServiceProvider services, AppSettings settings)
    {
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        // Not the settings host for a managed worker: connecting replaced it with the server's IP address.
        var managedName = lifecycle.IsManaged(settings) ? (await lifecycle.ReadRecordedAsync(settings.Database.Path, CancellationToken.None))?.Name : null;
        return managedName ?? settings.Processing.RemoteSsh.Host;
    }

    /// <summary>For an <c>immich run</c> the user limited, <see cref="StoppedRunMessage.CountImmichJobsLeft"/>; null otherwise.</summary>
    private static async Task<int?> ImmichJobsLeftAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command)
    {
        if (command.Kind != CliCommandKind.ImmichRun || command.Limit is null) return null;
        return StoppedRunMessage.CountImmichJobsLeft(await services.GetRequiredService<IJobRepository>().ListAsync(configuration.Settings.Database.Path, terminal: false, CancellationToken.None));
    }

    private static FailedQueueJob ToFailedQueueJob(JobRecord job, string message) => new(job.Id, job.SourcePath, job.FailureCategory ?? "ProcessingFailed", job.FailureMessage ?? message);

    private static void PrintFailedJobs(FailedQueueJob[] failedJobs)
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

    private static Task<RemoteWorkerInfo> EnsureWorkerReadyAsync(IServiceProvider services, LoadedConfiguration configuration, CancellationToken cancellationToken) =>
        services.GetRequiredService<IRemoteWorkerLifecycle>().EnsureReadyAsync(configuration.Settings, configuration.Settings.Database.Path, new InlineProgress<string>(Console.WriteLine), ConsolePrompts.ConfirmServerCreationAsync, cancellationToken);

    /// <summary>
    /// Runs a run command so that every way it ends, once it holds the run lock, decides the managed worker's fate: a stop
    /// now (Ctrl+C twice, or once with no video converting, whatever it interrupted: Immich discovery, the worker's creation,
    /// a prompt or a video), an error, or a return before anything was converted. A finished conversion decides inside the
    /// command, after its summary.
    /// </summary>
    private static async Task<int> RunGuardedAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, Func<Task<int>> run)
    {
        var state = services.GetRequiredService<RunWorkerState>();
        int exitCode;
        try
        {
            exitCode = await run();
        }
        catch (OperationCanceledException exception) when (state.Started && !state.Concluded)
        {
            // A failed delete still exits 9: the run stopped.
            await ConcludeOrReportAsync(() => ConcludeWorkerAsync(services, configuration, command, RunEnd.StoppedNow, interrupted: exception as JobInterruptedException));
            return (int)ExitCode.Cancelled;
        }
        catch (Exception exception) when (state.Started && !state.Concluded && exception is not OperationCanceledException)
        {
            // The error first, then what happens to the worker.
            exitCode = ReportFailure(exception);
            await ConcludeOrReportAsync(() => ConcludeWorkerAsync(services, configuration, command, RunEnd.Failed));
            return exitCode;
        }
        if (state.Started && !state.Concluded) await ConcludeOrReportAsync(() => ConcludeWorkerAsync(services, configuration, command, RunEnd.Finished));
        return exitCode;
    }

    /// <summary>
    /// An error while deciding the worker's fate (an unreadable state file, a busy database) is reported, and the run keeps
    /// the exit code of how it ended: a stop stays 9.
    /// </summary>
    private static async Task ConcludeOrReportAsync(Func<Task<bool>> conclude)
    {
        try
        {
            _ = await conclude();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = ReportFailure(exception);
        }
    }

    private static string GetVersion() => typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";
}
