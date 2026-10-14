using System.Globalization;
using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VideoOptimiser.Application.Calibration;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Calibration;
using VideoOptimiser.Infrastructure.Configuration;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Immich;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Processing;
using VideoOptimiser.Infrastructure.Scanning;
using VideoOptimiser.Cli;

NativeSqliteBootstrapper.Initialize();
return await CliApplication.RunAsync(args);

internal static partial class CliApplication
{
    // Chosen once from the real console before the command runs, because it depends on how this process was started
    // (terminal or redirected, per stream) and on --json. The writers take them as arguments so tests can pick their own.
    private static TerminalStyle OutputStyle { get; set; } = TerminalStyle.Plain;
    private static TerminalStyle ErrorStyle { get; set; } = TerminalStyle.Plain;

    /// <summary>An error or a refusal, in red on a terminal. Usage text and other help are written as they are.</summary>
    private static void WriteError(string? text) => Console.Error.WriteLine(ErrorStyle.Red(text ?? string.Empty));

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
                Console.WriteLine(Usage);
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
                Console.Error.WriteLine(Usage);
                return (int)ExitCode.InvalidArguments;
            }

            using var host = BuildHost(configureServices);
            using var cancellationSource = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellationSource.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                return await ExecuteAsync(host.Services, command, cancellationSource.Token);
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
        catch (FileNotFoundException exception)
        {
            WriteError(exception.Message);
            return (int)ExitCode.InvalidConfiguration;
        }
        catch (InvalidDataException exception)
        {
            WriteError($"Configuration could not be read: {exception.Message}");
            return (int)ExitCode.InvalidConfiguration;
        }
        catch (MediaProbeException exception)
        {
            WriteError(exception.Message);
            return (int)ExitCode.ProcessingFailure;
        }
        catch (ImmichApiException exception)
        {
            WriteError(exception.Message);
            return exception.Code is "ImmichApiKeyMissing" or "ImmichApiKeyRejected" or "ImmichPermissionMissing" or ImmichTrash.DisabledCode
                ? (int)ExitCode.InvalidConfiguration
                : (int)ExitCode.ProcessingFailure;
        }
        catch (RemoteWorkerCreationDeclinedException exception)
        {
            // A declined price is the user's choice, as with a declined finalize, not a failure. Jobs stay as they were.
            Console.WriteLine(exception.Message);
            return (int)ExitCode.Success;
        }
        catch (RemoteWorkerCapacityUnavailableException exception)
        {
            WriteError(exception.Message);
            WriteError("Check server type and location availability in Hetzner Console: https://console.hetzner.com/");
            return (int)ExitCode.ProcessingFailure;
        }
        catch (Exception exception)
        {
            WriteError($"Unexpected failure: {exception.GetBaseException().Message}");
            return (int)ExitCode.GeneralFailure;
        }
    }

    private static IHost BuildHost(Action<IServiceCollection>? configureServices)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IConfigurationLoader, YamlConfigurationLoader>();
        builder.Services.AddSingleton<IConfigurationTemplateWriter, YamlConfigurationTemplateWriter>();
        builder.Services.AddSingleton<ISettingsValidator, SettingsValidator>();
        builder.Services.AddSingleton<IDatabaseInitializer, SqliteDatabaseInitializer>();
        builder.Services.AddSingleton<IToolVerifier, ProcessToolVerifier>();
        builder.Services.AddSingleton<IRemoteEnvironmentVerifier, ProcessRemoteEnvironmentVerifier>();
        builder.Services.AddSingleton<IDoctorService, DoctorService>();
        builder.Services.AddSingleton<IImmichClientFactory, ImmichClientFactory>();
        builder.Services.AddSingleton<IFileReadinessService, FileReadinessService>();
        builder.Services.AddSingleton<Func<string, IMediaProbe>>(_ => ffprobePath => new FfprobeMediaProbe(ffprobePath));
        builder.Services.AddSingleton<Func<string, IMediaTimelineProbe>>(_ => ffprobePath => new FfprobeMediaTimelineProbe(ffprobePath));
        builder.Services.AddSingleton<IMediaProbeCache, SqliteMediaProbeCache>();
        builder.Services.AddSingleton<IFileScanner, FileScanner>();
        builder.Services.AddSingleton<Func<string, ICrfSearchClient>>(_ => abAv1Path => new AbAv1CrfSearchClient(abAv1Path));
        builder.Services.AddSingleton<Func<string, IVideoEncoder>>(_ => abAv1Path => new AbAv1VideoEncoder(abAv1Path));
        builder.Services.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
        builder.Services.AddSingleton<IRemoteWorkerLifecycle, HetznerRemoteWorkerLifecycle>();
        builder.Services.AddSingleton<IRemoteSetupService, RemoteSetupService>();
        builder.Services.AddSingleton<IProcessingSessionFactory, ProcessingSessionFactory>();
        builder.Services.AddSingleton<IOutputManifestStore, OutputManifestStore>();
        builder.Services.AddSingleton<IFileFingerprintService, FileFingerprintService>();
        builder.Services.AddSingleton<IJobRepository, SqliteJobRepository>();
        // calibrate promises to write nothing, so it gets its own read-only store and a probe cache that never saves.
        builder.Services.AddSingleton<ICalibrationService>(services => CalibrationService.ReadOnly(
            services.GetRequiredService<IFileReadinessService>(), services.GetRequiredService<Func<string, IMediaProbe>>(), services.GetRequiredService<IImmichClientFactory>()));
        builder.Services.AddSingleton<Func<string, IMediaDecodeTester>>(services => ffmpegPath => new FfmpegDecodeTester(ffmpegPath, services.GetRequiredService<IExternalProcessRunner>()));
        builder.Services.AddSingleton<IOutputValidationService, OutputValidationService>();
        builder.Services.AddSingleton<IJobProcessor, JobProcessor>();
        builder.Services.AddSingleton<IQueueService, QueueService>();
        builder.Services.AddSingleton<IImmichDiscoveryService>(services => new ImmichDiscoveryService(services.GetRequiredService<IImmichClientFactory>(), services.GetRequiredService<IJobRepository>(), services.GetRequiredService<Func<string, IMediaProbe>>(), services.GetRequiredService<IFileFingerprintService>()));
        builder.Services.AddSingleton<IImmichReuploadGuard>(services => new ImmichReuploadGuard(services.GetRequiredService<IImmichClientFactory>(), services.GetRequiredService<IJobRepository>()));
        builder.Services.AddSingleton<IImmichRunService, ImmichRunService>();
        builder.Services.AddSingleton<IJobRetryService, JobRetryService>();
        builder.Services.AddSingleton<ITemporaryOutputCleanupService, TemporaryOutputCleanupService>();
        builder.Services.AddSingleton<ImmichFinalizer>();
        builder.Services.AddSingleton<IFinalizationService, FinalizationService>();
        builder.Services.AddSingleton<ISafeFileInstaller, SafeFileInstaller>();
        builder.Services.AddSingleton<IInteractiveConfirmation, ConsoleInteractiveConfirmation>();
        builder.Services.AddSingleton<IManagedWorkerCleanupOrchestrator, ManagedWorkerCleanupOrchestrator>();
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
                var quality = ConfigInitQuestion.Choose(command.Quality, new ConsoleInteractiveConfirmation().IsAvailable, Console.In, Console.Out);
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
            CliCommandKind.Process => await RunProcessAsync(services, configuration, command, cancellationToken),
            CliCommandKind.QueueDiscover => await RunQueueDiscoverAsync(services.GetRequiredService<IQueueService>(), configuration, command.First, cancellationToken),
            CliCommandKind.QueueRun => await RunQueueRunAsync(services, configuration, command, cancellationToken),
            CliCommandKind.QueueList => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: false, command.Json, cancellationToken),
            CliCommandKind.QueueCancel => await RunQueueCancelAsync(services.GetRequiredService<IJobRepository>(), configuration, command, cancellationToken),
            CliCommandKind.QueueRetry => await RunQueueRetryAsync(services.GetRequiredService<IJobRetryService>(), configuration, command.JobId!, cancellationToken),
            CliCommandKind.Status => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: false, command.Json, cancellationToken),
            CliCommandKind.History => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: true, command.Json, cancellationToken),
            CliCommandKind.Validate => await RunValidateAsync(services, configuration, command.JobId!, cancellationToken),
            CliCommandKind.Finalize => await RunFinalizeAsync(services, configuration, command, cancellationToken),
            CliCommandKind.Cleanup => await RunCleanupAsync(services.GetRequiredService<ITemporaryOutputCleanupService>(), configuration, command.DryRun, cancellationToken),
            CliCommandKind.ImmichRun => await RunImmichRunAsync(services, configuration, command, cancellationToken),
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

    private static async Task<int> RunProcessAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var diagnostics = services.GetRequiredService<ISettingsValidator>().Validate(configuration.Settings);
        if (diagnostics.Count > 0)
        {
            foreach (var diagnostic in diagnostics)
            {
                WriteError($"[{diagnostic.Code}] {diagnostic.Message}");
            }

            return (int)ExitCode.InvalidConfiguration;
        }

        // A dry run writes nothing, so it runs beside a real one.
        using var runLock = command.DryRun ? null : TakeRunLock(configuration, "process");
        if (!command.DryRun && runLock is null) return (int)ExitCode.GeneralFailure;
        await PrintRunBannerAsync(services, configuration, command.ModeOverride, cancellationToken);
        var sourcePath = Path.GetFullPath(command.ProcessPath!);
        if (!File.Exists(sourcePath))
        {
            WriteError($"Original does not exist: {sourcePath}");
            return (int)ExitCode.ProcessingFailure;
        }
        if (command.DryRun)
        {
            Console.WriteLine($"Would create a job for: {sourcePath}");
            Console.WriteLine("Would run CRF search, encode a temporary AV1, then validate it. The original would not be changed.");
            return (int)ExitCode.Success;
        }

        var repository = services.GetRequiredService<IJobRepository>();
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        // As in QueueService.PrepareAsync: a finalisation holding its lock owns its Finalizing job.
        using (var finalizeLock = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(configuration.Settings.Database.Path), "run", out _))
        {
            await repository.MarkActiveJobsInterruptedAsync(configuration.Settings.Database.Path, includeFinalizing: finalizeLock is not null, cancellationToken);
        }
        var existingJob = await repository.FindOpenBySourceAsync(configuration.Settings.Database.Path, sourcePath, cancellationToken);
        var compatibility = existingJob is null ? null : JobCompatibility.Evaluate(existingJob, configuration.Settings);
        if (command.ModeOverride is not null && compatibility is not null && JobCompatibility.IsBoundToOtherMode(existingJob!, compatibility))
        {
            Console.WriteLine(JobCompatibility.SkippedMessage(existingJob!));
            return (int)ExitCode.Success;
        }
        if (compatibility?.Status == JobCompatibilityStatus.Adopted)
        {
            JobCompatibility.Apply(existingJob!, compatibility.ExpectedBinding);
            await repository.UpdateAsync(configuration.Settings.Database.Path, existingJob!, cancellationToken);
        }
        var canRunExistingJob = existingJob is null ||
            existingJob.Status is JobStatus.Queued or JobStatus.Interrupted && compatibility!.CanRun;
        // A server bills from the moment it exists, so it is created only for a job that will reach a remote stage (VOPT-68).
        var managedWork = lifecycle.IsManaged(configuration.Settings) && canRunExistingJob &&
            await services.GetRequiredService<IJobProcessor>().NeedsWorkerAsync(configuration.Settings.Database.Path, sourcePath, configuration.Settings, command.Force, cancellationToken);
        if (managedWork)
        {
            await EnsureWorkerReadyAsync(services, configuration, command, cancellationToken);
        }
        var progress = new JobProgressConsole(Console.Out, OutputStyle);
        progress.Report(JobProgress.Header(1, 1, sourcePath));
        JobProcessingResult result;
        try
        {
            result = await services.GetRequiredService<IJobProcessor>().ProcessAsync(configuration.Settings.Database.Path, sourcePath, configuration.Settings, command.Force, progress, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            await ReportStoppedRunAsync(services, configuration, command, exception);
            throw;
        }
        catch
        {
            await ApplyManagedWorkerDeletionPolicyAsync(services, configuration, command, managedWork, [], unreachableWorkerJob: null, unexpectedFailure: true, processRoute: true, cancellationToken);
            throw;
        }
        // A job that exists ended with an outcome line through the progress reporter. Only a failure before any job was
        // created (such as a missing original) has nothing else to say it.
        if (result.Job.Id == Guid.Empty)
        {
            if (result.ExitCode == ExitCode.Success) Console.WriteLine(result.Message);
            else WriteError(result.Message);
        }

        // An unreachable worker leaves the job Interrupted and resumable, so it is not a failed job. (Ctrl+C throws instead.)
        var unreachableWorkerJob = result.Job is { Status: JobStatus.Interrupted, FailureCategory: JobProcessor.RemoteUnavailableCategory } ? result.Job : null;
        FailedQueueJob[] failedJobs = result.ExitCode == ExitCode.Success || unreachableWorkerJob is not null ? [] : [ToFailedQueueJob(result.Job, result.Message)];
        PrintFailedJobs(failedJobs);
        if (result.Job.Status == JobStatus.ReadyToFinalize) Console.WriteLine($"Next: review the output, then run 'video-optimiser finalize {result.Job.Id:N}'.");
        await ApplyManagedWorkerDeletionPolicyAsync(services, configuration, command, managedWork, failedJobs, unreachableWorkerJob, unexpectedFailure: false, processRoute: true, cancellationToken);
        if (unreachableWorkerJob is not null) await ReportUnreachableManualHostAsync(services, configuration, command, unreachableWorkerJob);

        return (int)result.ExitCode;
    }

    private static async Task<int> RunQueueDiscoverAsync(IQueueService queue, LoadedConfiguration configuration, bool first, CancellationToken cancellationToken)
    {
        // Settings validation allows no roots when Immich is configured; scanning nothing would only print "Queued: 0".
        if (configuration.Settings.Watch.Roots.Count == 0)
        {
            WriteError("queue discover needs at least one watch.roots entry. For Immich videos, use 'immich run'.");
            return (int)ExitCode.InvalidConfiguration;
        }

        Console.WriteLine($"Scanning {OutputWords.Count(configuration.Settings.Watch.Roots.Count, "watch root", "watch roots")} for videos to convert; files not seen before are probed with ffprobe.");
        var result = await queue.DiscoverAsync(configuration.Settings.Database.Path, configuration.Settings, first, new QueueDiscoverConsole(Console.Out), cancellationToken);
        QueueDiscoverConsole.WriteSummary(Console.Out, result);
        return result.Issues == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }

    private static async Task<int> RunQueueCancelAsync(IJobRepository repository, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        JobRecord[] jobs = command.All
            // Not an interrupted finalisation: finalize resumes it, and cancelling it would only hide a finished encode.
            ? (await repository.ListAsync(configuration.Settings.Database.Path, terminal: false, cancellationToken)).Where(job => job.Status is JobStatus.Queued or JobStatus.Interrupted && job.ResumeStatus != JobStatus.Finalizing).ToArray()
            : Guid.TryParse(command.JobId, out var id) ? (await repository.GetAsync(configuration.Settings.Database.Path, id, cancellationToken) is { } foundJob ? [foundJob] : Array.Empty<JobRecord>()) : Array.Empty<JobRecord>();
        if (jobs.Length == 0) { Console.WriteLine("No cancellable jobs found."); return (int)ExitCode.Success; }
        foreach (var job in jobs)
        {
            // A ready job is cancellable only by ID, so --all never discards finished encodes. It is the way out for an Immich
            // job whose original went away, which would otherwise wait at ready forever.
            if (job.Status is not (JobStatus.Queued or JobStatus.Interrupted) && (command.All || job.Status != JobStatus.ReadyToFinalize)) { WriteError(NotCancellableMessage(job)); continue; }
            job.Status = JobStatus.Cancelled;
            job.FailureCategory = "Cancelled";
            job.FailureMessage = "Cancelled by user.";
            job.CompletedUtc = DateTimeOffset.UtcNow;
            await repository.UpdateAsync(configuration.Settings.Database.Path, job, cancellationToken);
            Console.WriteLine($"Cancelled  {job.Id:N}  {Path.GetFileName(job.SourcePath)}");
            if (job.ImmichNewAssetId is { } uploaded) Console.WriteLine($"The AV1 already uploaded to Immich as asset {uploaded} stays there; delete it in Immich if you do not want it.");
        }
        return (int)ExitCode.Success;
    }

    // Says why and what to do instead: a failed job's leftover output looks like something to cancel, but cleanup removes it.
    private static string NotCancellableMessage(JobRecord job) => job.Status switch
    {
        JobStatus.Failed => $"{job.Id:N} has already failed, so there is nothing to cancel. Run 'cleanup' to delete its temporary output.",
        JobStatus.Cancelled => $"{job.Id:N} is already cancelled. Run 'cleanup' to delete its temporary output.",
        JobStatus.Completed => $"{job.Id:N} has already been finalised, so there is nothing to cancel.",
        JobStatus.Skipped => $"{job.Id:N} was skipped, so there is nothing to cancel.",
        _ => $"{job.Id:N} is in progress ({job.Status}). It can be cancelled once its run stops (Ctrl+C)."
    };

    private static async Task<int> RunQueueRetryAsync(IJobRetryService retryService, LoadedConfiguration configuration, string jobId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(jobId, out var id))
        {
            WriteError("Job ID is invalid.");
            return (int)ExitCode.InvalidArguments;
        }
        var result = await retryService.RetryAsync(configuration.Settings.Database.Path, id, configuration.Settings, cancellationToken);
        if (!result.Succeeded)
        {
            WriteError(result.Error);
            return (int)ExitCode.InvalidArguments;
        }

        var replacement = result.Replacement!;
        if (replacement.SourceKind == JobSourceKind.Immich)
        {
            Console.WriteLine($"Created fresh queued job {replacement.Id:N}. Run 'immich run' to process it.");
            if (!File.Exists(replacement.SourcePath)) Console.WriteLine("Its staged copy is gone; 'immich run' downloads it from Immich again first.");
        }
        else
        {
            Console.WriteLine($"Created fresh queued job {replacement.Id:N}. Run 'queue run' to process it.");
        }
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunQueueRunAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (ReportInvalidSettings(services, configuration)) return (int)ExitCode.InvalidConfiguration;
        using var runLock = TakeRunLock(configuration, "queue run");
        if (runLock is null) return (int)ExitCode.GeneralFailure;
        await PrintRunBannerAsync(services, configuration, command.ModeOverride, cancellationToken);
        var preparation = await services.GetRequiredService<IQueueService>().PrepareAsync(configuration.Settings.Database.Path, configuration.Settings, JobSourceKind.File, modeOverridden: command.ModeOverride is not null, cancellationToken);
        var (ready, _, failures, workerUnreachable) = await ProcessPreparedJobsAsync(services, configuration, command, preparation, (ready, skipped, failures) =>
        {
            Console.WriteLine(OutcomeSummary.Run(ready, skipped, failures.Length, OutputStyle));
            PrintFailedJobs(failures);
            if (ready > 0) Console.WriteLine("Next: review the outputs, then run 'video-optimiser finalize --ready'.");
        }, cancellationToken);
        return (int)QueueExitCode.Calculate(ready, failures.Length, workerUnreachable);
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
    /// <c>'y'</c>; <see cref="ConsoleInteractiveConfirmation"/> refuses redirected input. No input at all declines.
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
        return Task.FromResult(ManagedWorkerDeletionPolicy.IsAffirmative(response?.Trim()));
    }

    private static bool ReportInvalidSettings(IServiceProvider services, LoadedConfiguration configuration)
    {
        var diagnostics = services.GetRequiredService<ISettingsValidator>().Validate(configuration.Settings);
        foreach (var diagnostic in diagnostics) WriteError($"[{diagnostic.Code}] {diagnostic.Message}");
        return diagnostics.Count > 0;
    }

    /// <summary>
    /// Processes prepared jobs, creating a managed worker only when there is work for it. <paramref name="report"/> prints the
    /// outcome before the worker's deletion policy speaks, so its retention messages follow the failures they refer to.
    /// </summary>
    private static async Task<(int Ready, int Skipped, FailedQueueJob[] Failures, bool WorkerUnreachable)> ProcessPreparedJobsAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, QueuePreparationResult preparation, Action<int, int, FailedQueueJob[]> report, CancellationToken cancellationToken)
    {
        foreach (var job in preparation.SkippedJobs) Console.WriteLine(JobCompatibility.SkippedMessage(job));
        // F8 (VOPT-70): finalize checks whether anything moved and resumes, so the run leaves these jobs alone.
        foreach (var job in preparation.InterruptedFinalizations) Console.WriteLine($"{Path.GetFileName(job.SourcePath)} was interrupted while being finalised. Run 'video-optimiser finalize --ready' to finish it.");
        if (preparation.RunnableJobs.Count == 0)
        {
            report(0, 0, [.. preparation.FailedJobs]);
            return (0, 0, [.. preparation.FailedJobs], false);
        }

        var queue = services.GetRequiredService<IQueueService>();
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        var managed = lifecycle.IsManaged(configuration.Settings);
        // A server bills from the moment it exists, so it is created only once a job will reach a remote stage (VOPT-68).
        // A run that never needs it prints no worker lines and leaves a worker kept from an earlier run alone.
        var managedWork = managed && await AnyJobNeedsWorkerAsync(services, configuration, preparation.RunnableJobs, cancellationToken);
        if (managedWork)
        {
            await EnsureWorkerReadyAsync(services, configuration, command, cancellationToken);
        }
        Console.WriteLine($"Converting {preparation.RunnableJobs.Count} {(preparation.RunnableJobs.Count == 1 ? "video" : "videos")}.");
        var progress = new JobProgressConsole(Console.Out, OutputStyle);
        QueueRunResult result;
        try
        {
            result = await queue.RunAsync(configuration.Settings.Database.Path, configuration.Settings, preparation.RunnableJobs, progress, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            await ReportStoppedRunAsync(services, configuration, command, exception);
            throw;
        }
        catch
        {
            await ApplyManagedWorkerDeletionPolicyAsync(services, configuration, command, managedWork, [], unreachableWorkerJob: null, unexpectedFailure: true, processRoute: false, cancellationToken);
            throw;
        }

        var failures = preparation.FailedJobs.Concat(result.FailedJobs).ToArray();
        if (result.UnreachableWorkerJob is not null)
        {
            Console.WriteLine();
            Console.WriteLine(UnreachableWorkerMessage.Stopped(await WorkerNameAsync(services, configuration.Settings), result.NotStarted + 1));
        }
        report(result.ReadyToFinalize, result.SkippedJobs.Count, failures);
        await ApplyManagedWorkerDeletionPolicyAsync(services, configuration, command, managedWork, failures, result.UnreachableWorkerJob, unexpectedFailure: false, processRoute: false, cancellationToken);
        if (result.UnreachableWorkerJob is not null)
        {
            await ReportUnreachableManualHostAsync(services, configuration, command, result.UnreachableWorkerJob);
        }
        else if (!managed && configuration.Settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Remote worker lifecycle is manual: after confirming all expected outputs are ready to finalise, delete the server; powering it off still bills.");
        }
        return (result.ReadyToFinalize, result.SkippedJobs.Count, failures, result.UnreachableWorkerJob is not null);
    }

    /// <summary>In run order, stopping at the first job that needs the worker: usually only the first job is probed twice.</summary>
    private static async Task<bool> AnyJobNeedsWorkerAsync(IServiceProvider services, LoadedConfiguration configuration, IReadOnlyList<JobRecord> jobs, CancellationToken cancellationToken)
    {
        var processor = services.GetRequiredService<IJobProcessor>();
        foreach (var job in jobs)
        {
            if (await processor.NeedsWorkerAsync(configuration.Settings.Database.Path, job.SourcePath, configuration.Settings, force: false, cancellationToken)) return true;
        }
        return false;
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

    /// <summary>Ctrl+C while the worker is created or bootstrapped: once a server exists it stays up and bills, so the user hears how to resume or delete it.</summary>
    private static async Task EnsureWorkerReadyAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        try
        {
            await services.GetRequiredService<IRemoteWorkerLifecycle>().EnsureReadyAsync(configuration.Settings, configuration.Settings.Database.Path, new InlineProgress<string>(Console.WriteLine), ConfirmServerCreationAsync, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            await ReportStoppedRunAsync(services, configuration, command, exception);
            throw;
        }
    }

    /// <summary>
    /// Ctrl+C on a remote run stops only this side (<see cref="StoppedRunMessage"/>) and deletes nothing: the worker is kept
    /// so the run can resume. The run's token is already cancelled, so the worker's name is read without it.
    /// </summary>
    private static async Task ReportStoppedRunAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, OperationCanceledException exception)
    {
        var settings = configuration.Settings;
        if (!settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase)) return;
        var managed = services.GetRequiredService<IRemoteWorkerLifecycle>().IsManaged(settings);
        var worker = await WorkerNameAsync(services, settings);
        if (string.IsNullOrEmpty(worker)) return;
        // Sets the message apart from the job's block, and ends a creation prompt that Ctrl+C left open.
        Console.WriteLine();
        foreach (var line in StoppedRunMessage.Lines(exception as JobInterruptedException, worker, managed, command, await ImmichJobsLeftAsync(services, configuration, command))) Console.WriteLine(line);
    }

    /// <param name="command">The command the user ran; the commands it prints keep its <c>--config</c> only when that was typed, since otherwise the worker's settings are found the same way without it.</param>
    /// <param name="unreachableWorkerJob">The job a worker that stopped answering left interrupted, or null.</param>
    private static async Task ApplyManagedWorkerDeletionPolicyAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, bool workerWasUsed, FailedQueueJob[] failedJobs, JobRecord? unreachableWorkerJob, bool unexpectedFailure, bool processRoute, CancellationToken cancellationToken)
    {
        var interrupted = unreachableWorkerJob is not null;
        var decision = ManagedWorkerDeletionPolicy.Decide(workerWasUsed, configuration.Settings.Processing.RemoteSsh.Hetzner.DeleteAfterRun, failedJobs.Length > 0, interrupted, unexpectedFailure);
        if (decision == ManagedWorkerDeletionDecision.RetainInterrupted)
        {
            Console.WriteLine();
            foreach (var line in UnreachableWorkerMessage.Lines(unreachableWorkerJob!, managed: true, command, await ImmichJobsLeftAsync(services, configuration, command))) Console.WriteLine(line);
        }
        else if (decision == ManagedWorkerDeletionDecision.RetainAfterUnexpectedFailure)
        {
            Console.WriteLine("Managed Hetzner worker retained after an unexpected processing error.");
        }
        else if (decision == ManagedWorkerDeletionDecision.RetainAfterFailure)
        {
            Console.WriteLine();
            Console.WriteLine("Managed Hetzner worker retained so the failed job can be inspected or retried.");
            Console.WriteLine($"Delete it when finished: {StoppedRunMessage.DeleteCommand(command)}");
        }
        var cleanup = services.GetRequiredService<IManagedWorkerCleanupOrchestrator>();
        var progress = new InlineProgress<string>(Console.WriteLine);
        if (processRoute)
        {
            _ = await cleanup.CleanUpAfterProcessAsync(configuration.Settings, configuration.Settings.Database.Path, workerWasUsed, failedJobs.Length > 0, interrupted, unexpectedFailure, progress, cancellationToken);
        }
        else
        {
            _ = await cleanup.CleanUpAfterQueueRunAsync(configuration.Settings, configuration.Settings.Database.Path, workerWasUsed, failedJobs.Length > 0, interrupted, unexpectedFailure, progress, cancellationToken);
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
        var missing = !File.Exists(job.SourcePath) ? FinalizationService.OriginalMissing(job) : !File.Exists(job.OutputPath) ? FinalizationService.Av1Missing(job) : null;
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
            .Where(job => job.Status == JobStatus.ReadyToFinalize || FinalizationService.IsInterruptedImmichFinalization(job) || FinalizationService.IsInterruptedFileFinalization(job))
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
    /// as for <c>queue discover</c>, because the report is missing the videos they left out.
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

    internal const string Usage = """
Usage:
  video-optimiser config init [--config <path>] [--quality archive|standard|compact]
  video-optimiser version
  video-optimiser config show [--config <path>]
  video-optimiser config validate [--config <path>]
  video-optimiser doctor [--config <path>] [--json]
  video-optimiser worker create [--config <path>]
  video-optimiser worker status [--config <path>]
  video-optimiser worker delete [--config <path>]
  video-optimiser remote setup [--config <path>]
  video-optimiser process <file> [--config <path>] [--force] [--dry-run] [--local | --remote]
  video-optimiser queue discover [--first] [--config <path>]
  video-optimiser queue run [--config <path>] [--local | --remote]
  video-optimiser queue list [--config <path>] [--json]
  video-optimiser queue cancel <job-id> [--config <path>]
  video-optimiser queue cancel --all [--config <path>]
  video-optimiser queue retry <job-id> [--config <path>]
  video-optimiser status [--config <path>] [--json]
  video-optimiser history [--config <path>] [--json]
  video-optimiser calibrate [--config <path>] [--json]
  video-optimiser validate <job-id> [--config <path>]
  video-optimiser finalize <job-id> [--config <path>]
  video-optimiser finalize --ready [--config <path>]
  video-optimiser cleanup [--dry-run] [--config <path>]
  video-optimiser immich run [--first | --limit <n>] [--config <path>] [--local | --remote]
  video-optimiser immich finalize [--config <path>]
""";

    private static string GetVersion() => typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";
}

internal enum CliCommandKind
{
    Invalid,
    Help,
    Version,
    ConfigInit,
    ConfigShow,
    ConfigValidate,
    Doctor,
    WorkerCreate,
    WorkerStatus,
    WorkerDelete,
    RemoteSetup,
    Process,
    QueueDiscover,
    QueueRun,
    QueueList,
    QueueCancel,
    QueueRetry,
    Status,
    History,
    Validate,
    Finalize,
    Cleanup,
    ImmichRun,
    ImmichFinalize,
    Calibrate
}

internal sealed record CliCommand(CliCommandKind Kind, string? ConfigurationPath, string? ScanPath, bool Recursive, bool First, bool All, bool Json, string? Error, string? ProcessPath = null, bool Force = false, bool DryRun = false, string? JobId = null, bool FinalizeReady = false, ProcessingModeOverride? ModeOverride = null, int? Limit = null, QualityLevel? Quality = null)
{
    public bool IsValid => Kind != CliCommandKind.Invalid;
    public bool ShowHelp => Kind == CliCommandKind.Help;

    public static CliCommand Parse(string[] args)
    {
        var remaining = new List<string>();
        string? configurationPath = null;
        var force = false;
        var dryRun = false;
        var first = false;
        int? limit = null;
        QualityLevel? quality = null;
        var all = false;
        var json = false;
        var finalizeReady = false;
        var modeOverrides = new List<ProcessingModeOverride>();

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config" when index + 1 < args.Length:
                    configurationPath = args[++index];
                    break;
                case "--config":
                    return Invalid("--config requires a path.");
                case "--json":
                    json = true;
                    break;
                case "--all":
                    all = true;
                    break;
                case "--first":
                    first = true;
                    break;
                case "--limit" when index + 1 < args.Length && int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0:
                    limit = parsed;
                    index++;
                    break;
                case "--limit":
                    return Invalid("--limit requires a whole number of 1 or more.");
                case "--quality" when index + 1 < args.Length && QualityLevels.TryParse(args[index + 1], out var level):
                    quality = level;
                    index++;
                    break;
                case "--quality":
                    return Invalid("--quality requires archive, standard or compact.");
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--ready":
                    finalizeReady = true;
                    break;
                case "--local":
                    modeOverrides.Add(ProcessingModeOverride.Local);
                    break;
                case "--remote":
                    modeOverrides.Add(ProcessingModeOverride.Remote);
                    break;
                case "--crf":
                    return Invalid("--crf is no longer supported. process selects CRF automatically.");
                case "--help" or "-h" or "help":
                    return new CliCommand(CliCommandKind.Help, null, null, false, false, false, false, null);
                case "--version":
                    return new CliCommand(CliCommandKind.Version, null, null, false, false, false, false, null);
                default:
                    remaining.Add(args[index]);
                    break;
            }
        }

        var kind = remaining switch
        {
            ["config", "init"] => CliCommandKind.ConfigInit,
            ["config", "show"] => CliCommandKind.ConfigShow,
            ["config", "validate"] => CliCommandKind.ConfigValidate,
            ["version"] => CliCommandKind.Version,
            ["doctor"] => CliCommandKind.Doctor,
            ["worker", "create"] => CliCommandKind.WorkerCreate,
            ["worker", "status"] => CliCommandKind.WorkerStatus,
            ["worker", "delete"] => CliCommandKind.WorkerDelete,
            ["remote", "setup"] => CliCommandKind.RemoteSetup,
            ["process", _] => CliCommandKind.Process,
            ["queue", "discover"] => CliCommandKind.QueueDiscover,
            ["queue", "run"] => CliCommandKind.QueueRun,
            ["queue", "list"] => CliCommandKind.QueueList,
            ["queue", "cancel"] or ["queue", "cancel", _] => CliCommandKind.QueueCancel,
            ["queue", "retry", _] => CliCommandKind.QueueRetry,
            ["status"] => CliCommandKind.Status,
            ["history"] => CliCommandKind.History,
            ["calibrate"] => CliCommandKind.Calibrate,
            ["validate", _] => CliCommandKind.Validate,
            ["finalize"] or ["finalize", _] => CliCommandKind.Finalize,
            ["cleanup"] => CliCommandKind.Cleanup,
            ["immich", "run"] => CliCommandKind.ImmichRun,
            ["immich", "finalize"] => CliCommandKind.ImmichFinalize,
            _ => CliCommandKind.Invalid
        };

        if (kind == CliCommandKind.Invalid)
        {
            return Invalid("Unknown or incomplete command.");
        }

        if (json && kind is not (CliCommandKind.Doctor or CliCommandKind.QueueList or CliCommandKind.Status or CliCommandKind.History or CliCommandKind.Calibrate))
        {
            return Invalid("--json is only supported by doctor, queue list, status, history and calibrate.");
        }

        if (first && kind is not (CliCommandKind.QueueDiscover or CliCommandKind.ImmichRun)) return Invalid("--first is only supported by queue discover and immich run.");
        if (limit is not null && kind != CliCommandKind.ImmichRun) return Invalid("--limit is only supported by immich run.");
        if (quality is not null && kind != CliCommandKind.ConfigInit) return Invalid("--quality is only supported by config init.");
        if (first && limit is not null) return Invalid("--first and --limit cannot be used together.");
        // --first is --limit 1 for immich run; queue discover keeps its own --first.
        if (first && kind == CliCommandKind.ImmichRun) limit = 1;
        if (all && kind != CliCommandKind.QueueCancel) return Invalid("--all is only supported by queue cancel.");

        if (force && kind != CliCommandKind.Process)
        {
            return Invalid("--force is only supported by process.");
        }

        if (dryRun && kind is not (CliCommandKind.Process or CliCommandKind.Cleanup)) return Invalid("--dry-run is only supported by process and cleanup.");
        if (finalizeReady && kind != CliCommandKind.Finalize) return Invalid("--ready is only supported by finalize.");
        if (finalizeReady && remaining.Count != 1) return Invalid("finalize --ready does not take a job ID.");
        if (kind == CliCommandKind.QueueCancel && !all && remaining.Count != 3) return Invalid("queue cancel requires a job ID or --all.");
        if (kind == CliCommandKind.QueueRetry && remaining.Count != 3) return Invalid("queue retry requires a job ID.");
        if (modeOverrides.Distinct().Count() > 1) return Invalid("--local and --remote cannot be used together.");
        var modeOverride = modeOverrides.FirstOrDefault();
        if (modeOverride is not null && kind is not (CliCommandKind.QueueRun or CliCommandKind.Process or CliCommandKind.ImmichRun))
        {
            return Invalid($"{modeOverride.Flag} is only supported by queue run, process and immich run.");
        }

        return new CliCommand(kind, configurationPath, null, false, first, all, json, null, kind == CliCommandKind.Process ? remaining[1] : null, force, dryRun, (kind is CliCommandKind.Validate or CliCommandKind.Finalize or CliCommandKind.QueueCancel or CliCommandKind.QueueRetry) && remaining.Count > 1 ? remaining[^1] : null, finalizeReady, modeOverride, limit, quality);
    }

    private static CliCommand Invalid(string message) => new(CliCommandKind.Invalid, null, null, false, false, false, false, message);
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed class ConsoleInteractiveConfirmation : IInteractiveConfirmation
{
    public bool IsAvailable
    {
        get
        {
            try { return !Console.IsInputRedirected; }
            catch (IOException) { return false; }
        }
    }

    public bool Confirm(string prompt)
    {
        if (!IsAvailable) return false;
        Console.Write(prompt);
        try
        {
            var response = Console.ReadLine();
            return ManagedWorkerDeletionPolicy.IsAffirmative(response);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
