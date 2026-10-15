using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VideoOptimiser.Application.Calibration;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Configuration;
using static CliLocks;
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
        if (command.Kind == CliCommandKind.ConfigInit)
        {
            var writer = services.GetRequiredService<IConfigurationTemplateWriter>();
            try
            {
                // Refuse before asking, so nobody answers a question for a file that will not be written.
                var target = writer.GetDestination(command.ConfigurationPath);
                if (File.Exists(target)) throw YamlConfigurationTemplateWriter.RefuseOverwrite(target);
                var quality = ConfigInitQuestion.Choose(command.Quality, new ConsoleInput().IsInteractive, Console.In, Console.Out);
                var destination = await writer.WriteAsync(command.ConfigurationPath, quality, cancellationToken);
                Console.WriteLine($"Created editable configuration template: {destination}");
                Console.WriteLine("Add watch.roots, then run 'video-optimiser config validate'.");
                return (int)ExitCode.Success;
            }
            catch (InvalidOperationException exception)
            {
                WriteError(exception.Message);
                return (int)ExitCode.GeneralFailure;
            }
        }

        var configurationLoader = services.GetRequiredService<IConfigurationLoader>();
        var configuration = await configurationLoader.LoadAsync(command.ConfigurationPath, cancellationToken);
        command.ModeOverride?.Apply(configuration.Settings);
        if (command.Kind is CliCommandKind.WorkerCreate or CliCommandKind.WorkerStatus or CliCommandKind.WorkerDelete
            && RequireManagedLifecycle(command.Kind, configuration) is { } refused)
        {
            return refused;
        }
        return command.Kind switch
        {
            CliCommandKind.ConfigShow => ShowConfiguration(configuration),
            CliCommandKind.ConfigValidate => ValidateConfiguration(services.GetRequiredService<ISettingsValidator>(), configuration),
            CliCommandKind.Doctor => await RunDoctorAsync(services.GetRequiredService<IDoctorService>(), configuration, command.Json, cancellationToken),
            CliCommandKind.WorkerCreate => await RunWorkerCreateAsync(services, configuration, cancellationToken),
            CliCommandKind.WorkerStatus => await RunWorkerStatusAsync(services, configuration, cancellationToken),
            CliCommandKind.WorkerDelete => await RunWorkerDeleteAsync(services, configuration, cancellationToken),
            CliCommandKind.RemoteSetup => (int)await services.GetRequiredService<IRemoteSetupService>().RunAsync(configuration, ConfirmAsync, new InlineProgress<string>(Console.WriteLine), cancellationToken),
            CliCommandKind.QueueAdd => await RunQueueAddAsync(services.GetRequiredService<IQueueService>(), configuration, command, cancellationToken),
            CliCommandKind.QueueAddFile => await RunQueueAddFileAsync(services, configuration, command, cancellationToken),
            CliCommandKind.Run => await RunGuardedAsync(services, configuration, command, () => RunRunAsync(services, configuration, command, cancellationToken)),
            CliCommandKind.QueueList => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: false, command.Json, cancellationToken),
            CliCommandKind.QueueHold => await RunQueueHoldAsync(services.GetRequiredService<IJobRepository>(), configuration, command, cancellationToken),
            CliCommandKind.QueueRelease => await RunQueueReleaseAsync(services.GetRequiredService<IJobRepository>(), configuration, command, cancellationToken),
            CliCommandKind.QueueNext => await RunQueueNextAsync(services.GetRequiredService<IJobRepository>(), configuration, command, cancellationToken),
            CliCommandKind.QueueDrop => await RunQueueDropAsync(services, configuration, command, cancellationToken),
            CliCommandKind.QueueRetry => await RunQueueRetryAsync(services.GetRequiredService<IJobRetryService>(), configuration, command, cancellationToken),
            CliCommandKind.Status => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: false, command.Json, cancellationToken),
            CliCommandKind.History => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: true, command.Json, cancellationToken),
            CliCommandKind.Validate => await RunValidateAsync(services, configuration, command.JobId!, cancellationToken),
            CliCommandKind.Finalize => await RunFinalizeAsync(services, configuration, command, cancellationToken),
            CliCommandKind.Cleanup => await RunCleanupAsync(services.GetRequiredService<ITemporaryOutputCleanupService>(), configuration, command.DryRun, cancellationToken),
            CliCommandKind.ImmichRun => await RunGuardedAsync(services, configuration, command, () => RunImmichRunAsync(services, configuration, command, cancellationToken)),
            CliCommandKind.ImmichFinalize => await RunImmichFinalizeAsync(services, configuration, cancellationToken),
            CliCommandKind.Calibrate => await RunCalibrateAsync(services, configuration, command.Json, cancellationToken),
            _ => (int)ExitCode.InvalidArguments
        };
    }

    private static int ShowConfiguration(LoadedConfiguration configuration)
    {
        Console.WriteLine($"Configuration path: {configuration.Path}");
        Console.WriteLine();
        Console.Write(configuration.RawYaml ?? "# Configuration content is not available.\n");
        return (int)ExitCode.Success;
    }

    private static int ValidateConfiguration(ISettingsValidator validator, LoadedConfiguration configuration)
    {
        var diagnostics = validator.Validate(configuration.Settings);
        if (diagnostics.Count == 0)
        {
            Console.WriteLine($"Valid configuration: {configuration.Path}");
            return (int)ExitCode.Success;
        }

        foreach (var diagnostic in diagnostics)
        {
            WriteError($"[{diagnostic.Code}] {diagnostic.Message}");
        }

        return (int)ExitCode.InvalidConfiguration;
    }

    private static async Task<int> RunDoctorAsync(IDoctorService doctorService, LoadedConfiguration configuration, bool json, CancellationToken cancellationToken)
    {
        var report = await doctorService.RunAsync(configuration, cancellationToken);
        if (json)
        {
            var output = new DoctorJsonOutput(
                configuration.Path,
                (int)report.ExitCode,
                report.Diagnostics.Select(diagnostic => new DoctorJsonDiagnostic(diagnostic.Category.ToString(), diagnostic.Status.ToString(), diagnostic.Code, diagnostic.Message)).ToArray());
            Console.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.DoctorJsonOutput));
        }
        else
        {
            foreach (var diagnostic in report.Diagnostics)
            {
                Console.WriteLine($"{diagnostic.Status,-7} {diagnostic.Category,-13} {diagnostic.Message}");
            }
        }

        return (int)report.ExitCode;
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
    /// Reads the answer even when input is redirected, like <c>finalize</c>, so an agent or script approves by piping
    /// <c>'y'</c>; the worker question refuses redirected input (<see cref="IConsoleInput.IsInteractive"/>). No input at all declines.
    /// </summary>
    private static Task<bool> ConfirmServerCreationAsync(RemoteWorkerCreationQuote quote, CancellationToken cancellationToken) =>
        ConfirmAsync(quote.FormatPrompt(), cancellationToken);

    private static Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken)
    {
        Console.Write(prompt);
        string? response;
        try { response = Console.ReadLine(); }
        catch (IOException) { response = null; }
        // Ctrl+C at the prompt ends the read with no answer; report it as a cancellation, not a decline.
        cancellationToken.ThrowIfCancellationRequested();
        if (response is null) Console.WriteLine();
        return Task.FromResult(WorkerEndPolicy.IsAffirmative(response));
    }

    private static bool ReportInvalidSettings(IServiceProvider services, LoadedConfiguration configuration)
    {
        var diagnostics = services.GetRequiredService<ISettingsValidator>().Validate(configuration.Settings);
        foreach (var diagnostic in diagnostics) WriteError($"[{diagnostic.Code}] {diagnostic.Message}");
        return diagnostics.Count > 0;
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
        services.GetRequiredService<IRemoteWorkerLifecycle>().EnsureReadyAsync(configuration.Settings, configuration.Settings.Database.Path, new InlineProgress<string>(Console.WriteLine), ConfirmServerCreationAsync, cancellationToken);

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

    /// <summary>
    /// The worker commands act on the managed server whatever <c>processing.mode</c> says, so a user who switched back to
    /// local can still delete a server that is billing (VOPT-56). <c>lifecycle</c> alone decides; the mode is set to remote
    /// in memory only, because the lifecycle treats a server as managed only in remote mode. Returns the exit code when it refuses.
    /// </summary>
    private static int? RequireManagedLifecycle(CliCommandKind kind, LoadedConfiguration configuration)
    {
        // An empty "lifecycle:" in YAML deserialises as null, so the value is compared with string.Equals.
        var lifecycle = configuration.Settings.Processing.RemoteSsh.Lifecycle;
        if (string.Equals(lifecycle, RemoteLifecycleModes.Hetzner, StringComparison.OrdinalIgnoreCase))
        {
            ProcessingModeOverride.Remote.Apply(configuration.Settings);
            return null;
        }
        var name = kind switch { CliCommandKind.WorkerCreate => "worker create", CliCommandKind.WorkerStatus => "worker status", _ => "worker delete" };
        var current = string.IsNullOrWhiteSpace(lifecycle) ? "it is not set." : $"it is \"{lifecycle}\".";
        var message = $"'{name}' needs processing.remoteSsh.lifecycle set to \"hetzner\" in {configuration.Path}; {current}";
        // worker status is a read-only question, so "not managed" is an answer on stdout, as it has always been.
        if (kind == CliCommandKind.WorkerStatus)
        {
            Console.WriteLine(message);
            return (int)ExitCode.Success;
        }
        WriteError(message);
        return (int)ExitCode.InvalidConfiguration;
    }

    private static async Task<int> RunWorkerCreateAsync(IServiceProvider services, LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        var diagnostics = services.GetRequiredService<ISettingsValidator>().Validate(configuration.Settings);
        if (diagnostics.Count > 0) { foreach (var diagnostic in diagnostics) WriteError($"[{diagnostic.Code}] {diagnostic.Message}"); return (int)ExitCode.InvalidConfiguration; }
        var worker = await lifecycle.EnsureReadyAsync(configuration.Settings, configuration.Settings.Database.Path, new InlineProgress<string>(Console.WriteLine), ConfirmServerCreationAsync, cancellationToken);
        Console.WriteLine($"Worker ready: {worker.Name} ({worker.ServerId}) {worker.Address}{DescribePlacement(worker)}");
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunWorkerStatusAsync(IServiceProvider services, LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        var worker = await lifecycle.GetAsync(configuration.Settings, configuration.Settings.Database.Path, cancellationToken);
        Console.WriteLine(worker is null ? "No managed Hetzner worker exists." : $"{worker.Name}  server {worker.ServerId}  {worker.Status}  {worker.Address}{DescribePlacement(worker)}");
        return (int)ExitCode.Success;
    }

    // A worker recovered from Hetzner without a stored state file can lack either.
    private static string DescribePlacement(RemoteWorkerInfo worker) =>
        worker.ServerType.Length == 0 ? string.Empty
        : worker.Location.Length == 0 ? $"  {worker.ServerType}"
        : $"  {worker.ServerType} in {worker.Location}";

    private static async Task<int> RunWorkerDeleteAsync(IServiceProvider services, LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        var deleted = await lifecycle.DeleteAsync(configuration.Settings, configuration.Settings.Database.Path, new InlineProgress<string>(Console.WriteLine), cancellationToken);
        if (!deleted) Console.WriteLine("No managed Hetzner worker exists.");
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunJobListAsync(IJobRepository repository, LoadedConfiguration configuration, bool terminal, bool json, CancellationToken cancellationToken)
    {
        var jobs = await repository.ListAsync(configuration.Settings.Database.Path, terminal, cancellationToken);
        if (json)
        {
            var output = jobs.Select(job => new JobJsonOutput(job.Id.ToString("N"), job.Status.ToString(), job.SourcePath, job.SourceKind == JobSourceKind.Immich ? "immich" : "file", job.ImmichAssetId, job.ImmichNewAssetId, job.Crf, job.OutputPath, job.PercentageSaved, job.FailureCategory, job.FailureMessage, job.UpdatedUtc)).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.JobJsonOutputArray));
        }
        else if (jobs.Count == 0)
        {
            Console.WriteLine(terminal ? "No job history." : "No active jobs.");
        }
        else
        {
            foreach (var job in jobs)
            {
                var suffix = job.FailureMessage is null ? string.Empty : $" — {job.FailureCategory}: {job.FailureMessage}";
                var savings = job.PercentageSaved is null ? string.Empty : $" {job.PercentageSaved:F1}% saved";
                var resume = TemporaryOutputClassifier.DescribeResume(job) is { } behaviour ? $" ({behaviour})" : string.Empty;
                // An Immich job's file name is the original's in Immich; the asset IDs find it there.
                var immich = job.ImmichAssetId is { } asset ? $" Immich {asset}{(job.ImmichNewAssetId is { } av1 ? $" -> {av1}" : string.Empty)}" : string.Empty;
                Console.WriteLine($"{job.Id:N}  {job.Status,-16} {Path.GetFileName(job.SourcePath)}{immich}{(job.Crf is null ? string.Empty : $" CRF {job.Crf}")}{savings}{suffix}{resume}");
            }
        }

        return (int)ExitCode.Success;
    }

    private static async Task<int> RunValidateAsync(IServiceProvider services, LoadedConfiguration configuration, string jobId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(jobId, out var id)) { WriteError("Job ID is invalid."); return (int)ExitCode.InvalidArguments; }
        var repository = services.GetRequiredService<IJobRepository>();
        var job = await repository.GetAsync(configuration.Settings.Database.Path, id, cancellationToken);
        if (job is null) { WriteError("Job or temporary output was not found."); return (int)ExitCode.ProcessingFailure; }
        // F7 (VOPT-60). A finished job's manifest must not be rewritten, and a failed one cannot become ready again.
        if (!(job.Status is JobStatus.ReadyToFinalize or JobStatus.Validating || job is { Status: JobStatus.Interrupted, ResumeStatus: JobStatus.Validating }))
        {
            WriteError($"validate re-checks a job that is ready to finalise; {job.Id:N} is {job.Status}.");
            return (int)ExitCode.ProcessingFailure;
        }
        if (job.OutputPath is null) { WriteError("Job or temporary output was not found."); return (int)ExitCode.ProcessingFailure; }
        // Validation would fail the job, and cleanup then deletes an AV1 that may be the only copy, so a missing file is
        // reported and the job left as it is.
        var missing = !File.Exists(job.SourcePath) ? FinalizationRules.OriginalMissing(job) : !File.Exists(job.OutputPath) ? FinalizationRules.Av1Missing(job) : null;
        if (missing is not null) { WriteError(missing); return (int)ExitCode.ProcessingFailure; }
        var store = services.GetRequiredService<IOutputManifestStore>();
        var manifest = await store.LoadAsync(job.OutputPath, cancellationToken);
        manifest.Validation = await services.GetRequiredService<IOutputValidationService>().ValidateAsync(manifest, configuration.Settings, cancellationToken);
        // From here the record and the job must agree, so Ctrl+C cannot leave one written without the other.
        await store.SaveAsync(manifest, CancellationToken.None);
        job.ValidationPassed = manifest.Validation.Passed;
        job.SourceSizeBytes = manifest.Validation.SourceSizeBytes;
        job.OutputSizeBytes = manifest.Validation.OutputSizeBytes;
        job.PercentageSaved = manifest.Validation.PercentageSaved;
        job.Status = manifest.Validation.Passed ? JobStatus.ReadyToFinalize : JobStatus.Failed;
        job.ResumeStatus = null;
        if (manifest.Validation.Passed) { job.FailureCategory = null; job.FailureMessage = null; }
        else { job.FailureCategory = "ValidationFailed"; job.FailureMessage = string.Join("; ", manifest.Validation.Failures); job.CompletedUtc = DateTimeOffset.UtcNow; }
        await repository.UpdateAsync(configuration.Settings.Database.Path, job, CancellationToken.None);
        Console.WriteLine(manifest.Validation.Passed ? $"Validation passed: saved {manifest.Validation.PercentageSaved:F1}%" : "Validation failed: " + string.Join("; ", manifest.Validation.Failures));
        return manifest.Validation.Passed ? (int)ExitCode.Success : (int)ExitCode.ValidationFailure;
    }

    private static async Task<int> RunFinalizeAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var repository = services.GetRequiredService<IJobRepository>();
        var finalizer = services.GetRequiredService<IFinalizationService>();
        using var finalizeLock = await TakeFinalizeLockAsync(configuration, "finalize", cancellationToken);
        if (finalizeLock is null) return (int)ExitCode.GeneralFailure;
        if (!command.FinalizeReady)
        {
            if (!Guid.TryParse(command.JobId, out var id)) { WriteError("Job ID is invalid."); return (int)ExitCode.InvalidArguments; }
            var result = await finalizer.FinalizeAsync(configuration.Settings.Database.Path, id, configuration.Settings, cancellationToken);
            if (result.ExitCode == ExitCode.Success) Console.WriteLine(result.Message);
            else WriteError(result.Message);
            return (int)result.ExitCode;
        }

        var ready = (await repository.ListAsync(configuration.Settings.Database.Path, terminal: false, cancellationToken))
            .Where(job => job.Status == JobStatus.ReadyToFinalize || FinalizationRules.IsInterruptedImmich(job) || FinalizationRules.IsInterruptedFile(job))
            .ToArray();
        if (ready.Length == 0) { Console.WriteLine("No jobs are ready to finalise."); return (int)ExitCode.Success; }
        foreach (var job in ready) Console.WriteLine(FinalizationConsole.DescribeReady(job));
        if (ready.Any(job => job.SourceKind == JobSourceKind.File)) Console.WriteLine(DescribeOriginalAction(configuration.Settings.Original));
        if (ready.Any(job => job.SourceKind == JobSourceKind.Immich)) Console.WriteLine("Each Immich original goes to Immich's trash once its AV1 is uploaded with its albums, tags and other relations.");
        Console.Write("Finalise these jobs? [y/N] ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Finalisation cancelled."); return (int)ExitCode.Success; }
        var report = new FinalizationConsole(Console.Out, OutputStyle, Console.Error, ErrorStyle);
        var failed = new List<(JobRecord Job, string Message)>();
        foreach (var job in ready)
        {
            var result = await finalizer.FinalizeAsync(configuration.Settings.Database.Path, job.Id, configuration.Settings, cancellationToken);
            if (!report.Outcome(job, result)) failed.Add((job, result.Message));
        }

        report.Summary(ready.Length, failed);
        return failed.Count == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }

    private static string DescribeOriginalAction(OriginalSettings original) => original.Action.ToLowerInvariant() switch
    {
        OriginalActions.Archive => Path.IsPathFullyQualified(original.ArchiveDirectory)
            ? $"Each original moves to {original.ArchiveDirectory} and its AV1 takes its place."
            : $"Each original moves to an {original.ArchiveDirectory} folder beside it and its AV1 takes its place.",
        OriginalActions.Keep => "Each original stays; its AV1 is installed beside it as <name>.av1<extension>.",
        _ => "Each original is deleted and its AV1 takes its place."
    };

    private static async Task<int> RunCleanupAsync(ITemporaryOutputCleanupService cleanup, LoadedConfiguration configuration, bool dryRun, CancellationToken cancellationToken)
    {
        var settings = configuration.Settings;
        var plan = await cleanup.FindAsync(settings.Database.Path, settings.Immich.IsConfigured ? settings.Immich.StagingDirectory : string.Empty, cancellationToken);
        foreach (var path in plan.UnknownJobFiles) Console.WriteLine($"Skipped  {path} (its job is not in this database)");
        if (plan.Count == 0) { Console.WriteLine("No abandoned temporary outputs or staged Immich originals found."); return (int)ExitCode.Success; }
        CleanupReport.WriteFiles(Console.Out, plan);
        Console.WriteLine(CleanupReport.Summary(plan));
        if (plan.StagedOriginals.Count > 0) Console.WriteLine("Staged Immich originals are local copies of Immich assets. After 'queue retry', 'immich run' downloads the copy again.");
        if (dryRun) return (int)ExitCode.Success;

        Console.Write("Delete these files? [y/N] ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Cleanup cancelled."); return (int)ExitCode.Success; }
        var result = cleanup.Delete(plan);
        foreach (var failure in result.Failures) WriteError($"Could not delete {failure}");
        Console.WriteLine($"Deleted {OutputWords.Count(result.Deleted, "file", "files")}, {HumanReadableValues.FormatSize(result.DeletedBytes)}.");
        return result.Failures.Count == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }

    /// <summary>
    /// <c>calibrate</c>: what the rules admit and keep out, and suggestions. Scan issues end in <see cref="ExitCode.PartialSuccess"/>,
    /// as for <c>queue add</c>, because the report is missing the videos they left out.
    /// </summary>
    private static async Task<int> RunCalibrateAsync(IServiceProvider services, LoadedConfiguration configuration, bool json, CancellationToken cancellationToken)
    {
        if (ReportInvalidSettings(services, configuration)) return (int)ExitCode.InvalidConfiguration;
        // With --json, stdout carries the JSON alone; an agent has no use for progress lines.
        var scanProgress = json ? null : new QueueDiscoverConsole(Console.Out);
        var immichProgress = json ? null : new ImmichSearchConsole(Console.Out);
        CalibrationRun run;
        try
        {
            run = await services.GetRequiredService<ICalibrationService>().RunAsync(configuration.Settings, scanProgress, immichProgress, cancellationToken);
        }
        catch (DatabaseOutdatedException exception)
        {
            WriteError($"{Path.GetFileName(exception.DatabasePath)} is from an older version of video-optimiser. Run 'video-optimiser status' once to upgrade it, then run calibrate again.");
            return (int)ExitCode.GeneralFailure;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(CalibrateConsole.ToJson(run.Report), CliJsonContext.Default.CalibrationJsonOutput));
        }
        else
        {
            if (scanProgress!.WroteLines || immichProgress!.WroteLines) Console.WriteLine();
            CalibrateConsole.Write(Console.Out, run.Report);
        }

        return run.ScanIssues == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }

    private static string GetVersion() => typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";
}
