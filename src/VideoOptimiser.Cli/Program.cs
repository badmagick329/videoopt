using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;
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
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var command = CliCommand.Parse(args);
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
                Console.Error.WriteLine(command.Error);
                Console.Error.WriteLine(Usage);
                return (int)ExitCode.InvalidArguments;
            }

            using var host = BuildHost();
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
            Console.Error.WriteLine(exception.Message);
            return (int)ExitCode.InvalidConfiguration;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine($"Configuration could not be read: {exception.Message}");
            return (int)ExitCode.InvalidConfiguration;
        }
        catch (ImmichApiException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return exception.Code is "ImmichApiKeyMissing" or "ImmichApiKeyRejected" or "ImmichPermissionMissing"
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
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Check server type and location availability in Hetzner Console: https://console.hetzner.com/");
            return (int)ExitCode.ProcessingFailure;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Unexpected failure: {exception.GetBaseException().Message}");
            return (int)ExitCode.GeneralFailure;
        }
    }

    private static IHost BuildHost()
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
        builder.Services.AddSingleton<IProcessingSessionFactory, ProcessingSessionFactory>();
        builder.Services.AddSingleton<IOutputManifestStore, OutputManifestStore>();
        builder.Services.AddSingleton<IFileFingerprintService, FileFingerprintService>();
        builder.Services.AddSingleton<IJobRepository, SqliteJobRepository>();
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
        return builder.Build();
    }

    private static async Task<int> ExecuteAsync(IServiceProvider services, CliCommand command, CancellationToken cancellationToken)
    {
        if (command.Kind == CliCommandKind.ConfigInit)
        {
            var writer = services.GetRequiredService<IConfigurationTemplateWriter>();
            try
            {
                var destination = await writer.WriteAsync(command.ConfigurationPath, cancellationToken);
                Console.WriteLine($"Created editable configuration template: {destination}");
                Console.WriteLine("Add watch.roots, then run 'video-optimiser config validate'.");
                return (int)ExitCode.Success;
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return (int)ExitCode.GeneralFailure;
            }
        }

        var configurationLoader = services.GetRequiredService<IConfigurationLoader>();
        var configuration = await configurationLoader.LoadAsync(command.ConfigurationPath, cancellationToken);
        return command.Kind switch
        {
            CliCommandKind.ConfigShow => ShowConfiguration(configuration),
            CliCommandKind.ConfigValidate => ValidateConfiguration(services.GetRequiredService<ISettingsValidator>(), configuration),
            CliCommandKind.Doctor => await RunDoctorAsync(services.GetRequiredService<IDoctorService>(), configuration, command.Json, cancellationToken),
            CliCommandKind.WorkerCreate => await RunWorkerCreateAsync(services, configuration, cancellationToken),
            CliCommandKind.WorkerStatus => await RunWorkerStatusAsync(services, configuration, cancellationToken),
            CliCommandKind.WorkerDelete => await RunWorkerDeleteAsync(services, configuration, cancellationToken),
            CliCommandKind.Process => await RunProcessAsync(services, configuration, command, cancellationToken),
            CliCommandKind.QueueDiscover => await RunQueueDiscoverAsync(services.GetRequiredService<IQueueService>(), configuration, command.First, cancellationToken),
            CliCommandKind.QueueRun => await RunQueueRunAsync(services, configuration, cancellationToken),
            CliCommandKind.QueueList => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: false, command.Json, cancellationToken),
            CliCommandKind.QueueCancel => await RunQueueCancelAsync(services.GetRequiredService<IJobRepository>(), configuration, command, cancellationToken),
            CliCommandKind.QueueRetry => await RunQueueRetryAsync(services.GetRequiredService<IJobRetryService>(), configuration, command.JobId!, cancellationToken),
            CliCommandKind.Status => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: false, command.Json, cancellationToken),
            CliCommandKind.History => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: true, command.Json, cancellationToken),
            CliCommandKind.Validate => await RunValidateAsync(services, configuration, command.JobId!, cancellationToken),
            CliCommandKind.Finalize => await RunFinalizeAsync(services, configuration, command, cancellationToken),
            CliCommandKind.Cleanup => await RunCleanupAsync(services.GetRequiredService<ITemporaryOutputCleanupService>(), configuration, command.DryRun, cancellationToken),
            CliCommandKind.ImmichRun => await RunImmichRunAsync(services, configuration, command.First, cancellationToken),
            CliCommandKind.ImmichFinalize => await RunImmichFinalizeAsync(services, configuration, cancellationToken),
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
            Console.Error.WriteLine($"[{diagnostic.Code}] {diagnostic.Message}");
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
                Console.Error.WriteLine($"[{diagnostic.Code}] {diagnostic.Message}");
            }

            return (int)ExitCode.InvalidConfiguration;
        }

        await PrintRunBannerAsync(services, configuration, cancellationToken);
        var sourcePath = Path.GetFullPath(command.ProcessPath!);
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"Original does not exist: {sourcePath}");
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
        await repository.MarkActiveJobsInterruptedAsync(configuration.Settings.Database.Path, cancellationToken);
        var existingJob = await repository.FindOpenBySourceAsync(configuration.Settings.Database.Path, sourcePath, cancellationToken);
        var compatibility = existingJob is null ? null : JobCompatibility.Evaluate(existingJob, configuration.Settings);
        if (compatibility?.Status == JobCompatibilityStatus.Adopted)
        {
            JobCompatibility.Apply(existingJob!, compatibility.ExpectedBinding);
            await repository.UpdateAsync(configuration.Settings.Database.Path, existingJob!, cancellationToken);
        }
        var canRunExistingJob = existingJob is null ||
            existingJob.Status is JobStatus.Queued or JobStatus.Interrupted && compatibility!.CanRun;
        var managedWork = lifecycle.IsManaged(configuration.Settings) && canRunExistingJob;
        if (managedWork)
        {
            await lifecycle.EnsureReadyAsync(configuration.Settings, configuration.Settings.Database.Path, new InlineProgress<string>(Console.WriteLine), ConfirmServerCreationAsync, cancellationToken);
        }
        Console.WriteLine($"Processing: {sourcePath}");
        var progress = new InlineProgress<CrfSearchOutput>(update => Console.WriteLine(update.Text));
        JobProcessingResult result;
        try
        {
            result = await services.GetRequiredService<IJobProcessor>().ProcessAsync(configuration.Settings.Database.Path, sourcePath, configuration.Settings, command.Force, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await ApplyManagedWorkerDeletionPolicyAsync(services, configuration, managedWork, [], interrupted: true, unexpectedFailure: false, processRoute: true, cancellationToken);
            throw;
        }
        catch
        {
            await ApplyManagedWorkerDeletionPolicyAsync(services, configuration, managedWork, [], interrupted: false, unexpectedFailure: true, processRoute: true, cancellationToken);
            throw;
        }
        if (result.ExitCode == ExitCode.Success)
        {
            Console.WriteLine(result.Message);
        }
        else
        {
            Console.Error.WriteLine(result.Message);
        }

        if (result.ExitCode != ExitCode.Success) PrintFailedJobs([ToFailedQueueJob(result.Job, result.Message)]);
        await ApplyManagedWorkerDeletionPolicyAsync(
            services,
            configuration,
            managedWork,
            result.ExitCode == ExitCode.Success ? [] : [ToFailedQueueJob(result.Job, result.Message)],
            result.Job.Status == JobStatus.Interrupted,
            unexpectedFailure: false,
            processRoute: true,
            cancellationToken);

        return (int)result.ExitCode;
    }

    private static async Task<int> RunQueueDiscoverAsync(IQueueService queue, LoadedConfiguration configuration, bool first, CancellationToken cancellationToken)
    {
        // Settings validation allows no roots when Immich is configured; scanning nothing would only print "Queued: 0".
        if (configuration.Settings.Watch.Roots.Count == 0)
        {
            Console.Error.WriteLine("queue discover needs at least one watch.roots entry. For Immich videos, use 'immich run'.");
            return (int)ExitCode.InvalidConfiguration;
        }

        var progress = new InlineProgress<ScanProgress>(update => Console.WriteLine(FormatScanProgress(update)));
        var result = await queue.DiscoverAsync(configuration.Settings.Database.Path, configuration.Settings, first, progress, cancellationToken);
        foreach (var path in result.QueuedPaths) Console.WriteLine($"Queued  {path}");
        Console.WriteLine($"Queued: {result.QueuedPaths.Count}. Already queued: {result.AlreadyQueued}. Already converted: {result.AlreadyConverted}. Previously failed or cancelled: {result.PreviouslyFailed}. Cache hits: {result.CacheHits}. Real probes: {result.RealProbes}. Issues: {result.Issues}.");
        if (result.PreviouslyFailed > 0) Console.WriteLine("Failed or cancelled originals stay out of the queue until they change. 'history' lists their jobs; 'queue retry <job-id>' queues one again.");
        return result.Issues == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }

    private static string FormatScanProgress(ScanProgress update) =>
        update.Stage == "Issue"
            ? string.IsNullOrWhiteSpace(update.Path)
                ? $"{update.Stage}: {update.Message}"
                : $"{update.Stage}: {update.Path} — {update.Message}"
            : string.IsNullOrWhiteSpace(update.Path)
            ? $"{update.Stage}: {update.Message}"
            : $"{update.Stage}: {update.Path}";

    private static async Task<int> RunQueueCancelAsync(IJobRepository repository, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        JobRecord[] jobs = command.All
            ? (await repository.ListAsync(configuration.Settings.Database.Path, terminal: false, cancellationToken)).Where(job => job.Status is JobStatus.Queued or JobStatus.Interrupted).ToArray()
            : Guid.TryParse(command.JobId, out var id) ? (await repository.GetAsync(configuration.Settings.Database.Path, id, cancellationToken) is { } foundJob ? [foundJob] : Array.Empty<JobRecord>()) : Array.Empty<JobRecord>();
        if (jobs.Length == 0) { Console.WriteLine("No cancellable jobs found."); return (int)ExitCode.Success; }
        foreach (var job in jobs)
        {
            // A ready job is cancellable only by ID, so --all never discards finished encodes. It is the way out for an Immich
            // job whose original went away, which would otherwise wait at ready forever.
            if (job.Status is not (JobStatus.Queued or JobStatus.Interrupted) && (command.All || job.Status != JobStatus.ReadyToFinalize)) { Console.Error.WriteLine($"{job.Id:N} is not cancellable."); continue; }
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

    private static async Task<int> RunQueueRetryAsync(IJobRetryService retryService, LoadedConfiguration configuration, string jobId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(jobId, out var id))
        {
            Console.Error.WriteLine("Job ID is invalid.");
            return (int)ExitCode.InvalidArguments;
        }
        var result = await retryService.RetryAsync(configuration.Settings.Database.Path, id, configuration.Settings, cancellationToken);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine(result.Error);
            return (int)ExitCode.InvalidArguments;
        }

        Console.WriteLine($"Created fresh queued job {result.Replacement!.Id:N}. Run 'queue run' to process it.");
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunQueueRunAsync(IServiceProvider services, LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        if (ReportInvalidSettings(services, configuration)) return (int)ExitCode.InvalidConfiguration;
        await PrintRunBannerAsync(services, configuration, cancellationToken);
        var preparation = await services.GetRequiredService<IQueueService>().PrepareAsync(configuration.Settings.Database.Path, configuration.Settings, JobSourceKind.File, cancellationToken);
        var (ready, failures) = await ProcessPreparedJobsAsync(services, configuration, preparation, (ready, failures) =>
        {
            Console.WriteLine($"Ready to finalise: {ready}. Failed: {failures.Length}.");
            PrintFailedJobs(failures);
        }, cancellationToken);
        return (int)QueueExitCode.Calculate(ready, failures.Length);
    }

    /// <summary>
    /// Prints <see cref="RunBanner"/> before any download or encoding, so a config still set to <c>remoteSsh</c> is noticed
    /// before it costs anything. A retained managed worker comes from the local state file, with no Hetzner API call.
    /// </summary>
    private static async Task PrintRunBannerAsync(IServiceProvider services, LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        var worker = lifecycle.IsManaged(configuration.Settings)
            ? await lifecycle.ReadRecordedAsync(configuration.Settings.Database.Path, cancellationToken)
            : null;
        Console.WriteLine(RunBanner.Describe(configuration.Settings, worker));
    }

    /// <summary>
    /// Reads the answer even when input is redirected, like <c>finalize</c>, so an agent or script approves by piping
    /// <c>'y'</c>; <see cref="ConsoleInteractiveConfirmation"/> refuses redirected input. No input at all declines.
    /// </summary>
    private static Task<bool> ConfirmServerCreationAsync(RemoteWorkerCreationQuote quote, CancellationToken cancellationToken)
    {
        Console.Write(quote.FormatPrompt());
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
        foreach (var diagnostic in diagnostics) Console.Error.WriteLine($"[{diagnostic.Code}] {diagnostic.Message}");
        return diagnostics.Count > 0;
    }

    /// <summary>
    /// Processes prepared jobs, creating a managed worker only when there is work for it. <paramref name="report"/> prints the
    /// outcome before the worker's deletion policy speaks, so its retention messages follow the failures they refer to.
    /// </summary>
    private static async Task<(int Ready, FailedQueueJob[] Failures)> ProcessPreparedJobsAsync(IServiceProvider services, LoadedConfiguration configuration, QueuePreparationResult preparation, Action<int, FailedQueueJob[]> report, CancellationToken cancellationToken)
    {
        if (preparation.RunnableJobs.Count == 0)
        {
            report(0, [.. preparation.FailedJobs]);
            return (0, [.. preparation.FailedJobs]);
        }

        var queue = services.GetRequiredService<IQueueService>();
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        var managedWork = lifecycle.IsManaged(configuration.Settings);
        if (managedWork)
        {
            await lifecycle.EnsureReadyAsync(configuration.Settings, configuration.Settings.Database.Path, new InlineProgress<string>(Console.WriteLine), ConfirmServerCreationAsync, cancellationToken);
        }
        Console.WriteLine("Running queued jobs.");
        var progress = new InlineProgress<CrfSearchOutput>(update => Console.WriteLine(update.Text));
        QueueRunResult result;
        try
        {
            result = await queue.RunAsync(configuration.Settings.Database.Path, configuration.Settings, preparation.RunnableJobs, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await ApplyManagedWorkerDeletionPolicyAsync(services, configuration, managedWork, [], interrupted: true, unexpectedFailure: false, processRoute: false, cancellationToken);
            throw;
        }
        catch
        {
            await ApplyManagedWorkerDeletionPolicyAsync(services, configuration, managedWork, [], interrupted: false, unexpectedFailure: true, processRoute: false, cancellationToken);
            throw;
        }

        var failures = preparation.FailedJobs.Concat(result.FailedJobs).ToArray();
        report(result.ReadyToFinalize, failures);
        await ApplyManagedWorkerDeletionPolicyAsync(services, configuration, managedWork, failures, result.Interrupted, unexpectedFailure: false, processRoute: false, cancellationToken);
        if (!managedWork && configuration.Settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Remote worker lifecycle is manual: after confirming all expected outputs are ready to finalise, delete the server; powering it off still bills.");
        }
        return (result.ReadyToFinalize, failures);
    }

    private static FailedQueueJob ToFailedQueueJob(JobRecord job, string message) => new(job.Id, job.SourcePath, job.FailureCategory ?? "ProcessingFailed", job.FailureMessage ?? message);

    private static void PrintFailedJobs(FailedQueueJob[] failedJobs)
    {
        if (failedJobs.Length == 0) return;
        Console.WriteLine();
        Console.WriteLine("Failed jobs:");
        foreach (var job in failedJobs)
        {
            Console.WriteLine($"{job.Id:N}  {Path.GetFileName(job.SourcePath)}");
            Console.WriteLine($"{job.FailureCategory}: {job.FailureMessage}");
        }
    }

    private static async Task ApplyManagedWorkerDeletionPolicyAsync(IServiceProvider services, LoadedConfiguration configuration, bool workerWasUsed, FailedQueueJob[] failedJobs, bool interrupted, bool unexpectedFailure, bool processRoute, CancellationToken cancellationToken)
    {
        var decision = ManagedWorkerDeletionPolicy.Decide(workerWasUsed, configuration.Settings.Processing.RemoteSsh.Hetzner.DeleteAfterRun, failedJobs.Length > 0, interrupted, unexpectedFailure);
        if (decision == ManagedWorkerDeletionDecision.RetainInterrupted)
        {
            Console.WriteLine("Managed Hetzner worker retained so interrupted jobs can resume.");
        }
        else if (decision == ManagedWorkerDeletionDecision.RetainAfterUnexpectedFailure)
        {
            Console.WriteLine("Managed Hetzner worker retained after an unexpected processing error.");
        }
        else if (decision == ManagedWorkerDeletionDecision.RetainAfterFailure)
        {
            Console.WriteLine();
            Console.WriteLine("Managed Hetzner worker retained so the failed job can be inspected or retried.");
            Console.WriteLine("Delete it manually when finished:");
            Console.WriteLine(@".\artifacts\local\video-optimiser.exe worker delete --config .\video-optimiser.yaml");
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

    private static async Task<int> RunWorkerCreateAsync(IServiceProvider services, LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        if (!lifecycle.IsManaged(configuration.Settings)) { Console.Error.WriteLine("Configure processing.remoteSsh.lifecycle as hetzner first."); return (int)ExitCode.InvalidConfiguration; }
        var diagnostics = services.GetRequiredService<ISettingsValidator>().Validate(configuration.Settings);
        if (diagnostics.Count > 0) { foreach (var diagnostic in diagnostics) Console.Error.WriteLine($"[{diagnostic.Code}] {diagnostic.Message}"); return (int)ExitCode.InvalidConfiguration; }
        var worker = await lifecycle.EnsureReadyAsync(configuration.Settings, configuration.Settings.Database.Path, new InlineProgress<string>(Console.WriteLine), ConfirmServerCreationAsync, cancellationToken);
        Console.WriteLine($"Worker ready: {worker.Name} ({worker.ServerId}) {worker.Address}{DescribePlacement(worker)}");
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunWorkerStatusAsync(IServiceProvider services, LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        if (!lifecycle.IsManaged(configuration.Settings)) { Console.WriteLine("Managed Hetzner lifecycle is not enabled."); return (int)ExitCode.Success; }
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
        if (!lifecycle.IsManaged(configuration.Settings)) { Console.Error.WriteLine("Managed Hetzner lifecycle is not enabled."); return (int)ExitCode.InvalidConfiguration; }
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
                var immich = job.ImmichAssetId is { } asset ? $" Immich {asset}{(job.ImmichNewAssetId is { } av1 ? $" → {av1}" : string.Empty)}" : string.Empty;
                Console.WriteLine($"{job.Id:N}  {job.Status,-16} {Path.GetFileName(job.SourcePath)}{immich}{(job.Crf is null ? string.Empty : $" CRF {job.Crf}")}{savings}{suffix}{resume}");
            }
        }

        return (int)ExitCode.Success;
    }

    private static async Task<int> RunValidateAsync(IServiceProvider services, LoadedConfiguration configuration, string jobId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(jobId, out var id)) { Console.Error.WriteLine("Job ID is invalid."); return (int)ExitCode.InvalidArguments; }
        var repository = services.GetRequiredService<IJobRepository>();
        var job = await repository.GetAsync(configuration.Settings.Database.Path, id, cancellationToken);
        if (job?.OutputPath is null) { Console.Error.WriteLine("Job or temporary output was not found."); return (int)ExitCode.ProcessingFailure; }
        var store = services.GetRequiredService<IOutputManifestStore>();
        var manifest = await store.LoadAsync(job.OutputPath, cancellationToken);
        manifest.Validation = await services.GetRequiredService<IOutputValidationService>().ValidateAsync(manifest, configuration.Settings, cancellationToken);
        await store.SaveAsync(manifest, cancellationToken);
        job.ValidationPassed = manifest.Validation.Passed;
        job.SourceSizeBytes = manifest.Validation.SourceSizeBytes;
        job.OutputSizeBytes = manifest.Validation.OutputSizeBytes;
        job.PercentageSaved = manifest.Validation.PercentageSaved;
        job.Status = manifest.Validation.Passed ? JobStatus.ReadyToFinalize : JobStatus.Failed;
        if (!manifest.Validation.Passed) { job.FailureCategory = "ValidationFailed"; job.FailureMessage = string.Join("; ", manifest.Validation.Failures); job.CompletedUtc = DateTimeOffset.UtcNow; }
        await repository.UpdateAsync(configuration.Settings.Database.Path, job, cancellationToken);
        Console.WriteLine(manifest.Validation.Passed ? $"Validation passed: saved {manifest.Validation.PercentageSaved:F1}%" : "Validation failed: " + string.Join("; ", manifest.Validation.Failures));
        return manifest.Validation.Passed ? (int)ExitCode.Success : (int)ExitCode.ValidationFailure;
    }

    private static async Task<int> RunFinalizeAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var repository = services.GetRequiredService<IJobRepository>();
        var finalizer = services.GetRequiredService<IFinalizationService>();
        if (!command.FinalizeReady)
        {
            if (!Guid.TryParse(command.JobId, out var id)) { Console.Error.WriteLine("Job ID is invalid."); return (int)ExitCode.InvalidArguments; }
            var result = await finalizer.FinalizeAsync(configuration.Settings.Database.Path, id, configuration.Settings, cancellationToken);
            (result.ExitCode == ExitCode.Success ? Console.Out : Console.Error).WriteLine(result.Message);
            return (int)result.ExitCode;
        }

        var ready = (await repository.ListAsync(configuration.Settings.Database.Path, terminal: false, cancellationToken))
            .Where(job => job.Status == JobStatus.ReadyToFinalize || FinalizationService.IsInterruptedImmichFinalization(job))
            .ToArray();
        if (ready.Length == 0) { Console.WriteLine("No jobs are ready to finalise."); return (int)ExitCode.Success; }
        foreach (var job in ready) Console.WriteLine($"{job.Id:N}  {Path.GetFileName(job.SourcePath)}");
        if (ready.Any(job => job.SourceKind == JobSourceKind.File)) Console.WriteLine(DescribeOriginalAction(configuration.Settings.Original));
        if (ready.Any(job => job.SourceKind == JobSourceKind.Immich)) Console.WriteLine("Each Immich original goes to Immich's trash once its AV1 is uploaded with its albums, tags and other relations.");
        Console.Write("Finalise these jobs? [y/N] ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Finalisation cancelled."); return (int)ExitCode.Success; }
        var failures = 0;
        foreach (var job in ready)
        {
            var result = await finalizer.FinalizeAsync(configuration.Settings.Database.Path, job.Id, configuration.Settings, cancellationToken);
            (result.ExitCode == ExitCode.Success ? Console.Out : Console.Error).WriteLine($"{job.Id:N}: {result.Message}");
            if (result.ExitCode != ExitCode.Success) failures++;
        }
        return failures == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
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
        var plan = await cleanup.FindAsync(configuration.Settings.Database.Path, cancellationToken);
        foreach (var path in plan.UnknownJobFiles) Console.WriteLine($"Skipped  {path} (its job is not in this database)");
        if (plan.Abandoned.Count == 0) { Console.WriteLine("No abandoned temporary outputs found."); return (int)ExitCode.Success; }
        foreach (var file in plan.Abandoned) Console.WriteLine($"{HumanReadableValues.FormatSize(file.SizeBytes),10}  {file.Path} ({file.Reason})");
        Console.WriteLine($"Abandoned temporary outputs: {plan.Abandoned.Count}, {HumanReadableValues.FormatSize(plan.TotalBytes)}.");
        if (dryRun) return (int)ExitCode.Success;

        Console.Write("Delete these files? [y/N] ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Cleanup cancelled."); return (int)ExitCode.Success; }
        var result = cleanup.Delete(plan);
        foreach (var failure in result.Failures) Console.Error.WriteLine($"Could not delete {failure}");
        Console.WriteLine($"Deleted {result.Deleted} files, {HumanReadableValues.FormatSize(result.DeletedBytes)}.");
        return result.Failures.Count == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }

    internal const string Usage = """
Usage:
  video-optimiser config init [--config <path>]
  video-optimiser version
  video-optimiser config show [--config <path>]
  video-optimiser config validate [--config <path>]
  video-optimiser doctor [--config <path>] [--json]
  video-optimiser worker create [--config <path>]
  video-optimiser worker status [--config <path>]
  video-optimiser worker delete [--config <path>]
  video-optimiser process <file> [--config <path>] [--force] [--dry-run]
  video-optimiser queue discover [--first] [--config <path>]
  video-optimiser queue run [--config <path>]
  video-optimiser queue list [--config <path>] [--json]
  video-optimiser queue cancel <job-id> [--config <path>]
  video-optimiser queue cancel --all [--config <path>]
  video-optimiser queue retry <job-id> [--config <path>]
  video-optimiser status [--config <path>] [--json]
  video-optimiser history [--config <path>] [--json]
  video-optimiser validate <job-id> [--config <path>]
  video-optimiser finalize <job-id> [--config <path>]
  video-optimiser finalize --ready [--config <path>]
  video-optimiser cleanup [--dry-run] [--config <path>]
  video-optimiser immich run [--first] [--config <path>]
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
    ImmichFinalize
}

internal sealed record CliCommand(CliCommandKind Kind, string? ConfigurationPath, string? ScanPath, bool Recursive, bool First, bool All, bool Json, string? Error, string? ProcessPath = null, bool Force = false, bool DryRun = false, string? JobId = null, bool FinalizeReady = false)
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
        var all = false;
        var json = false;
        var finalizeReady = false;

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
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--ready":
                    finalizeReady = true;
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
            ["process", _] => CliCommandKind.Process,
            ["queue", "discover"] => CliCommandKind.QueueDiscover,
            ["queue", "run"] => CliCommandKind.QueueRun,
            ["queue", "list"] => CliCommandKind.QueueList,
            ["queue", "cancel"] or ["queue", "cancel", _] => CliCommandKind.QueueCancel,
            ["queue", "retry", _] => CliCommandKind.QueueRetry,
            ["status"] => CliCommandKind.Status,
            ["history"] => CliCommandKind.History,
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

        if (json && kind is not (CliCommandKind.Doctor or CliCommandKind.QueueList or CliCommandKind.Status or CliCommandKind.History))
        {
            return Invalid("--json is only supported by doctor, scan, status, and history.");
        }

        if (first && kind is not (CliCommandKind.QueueDiscover or CliCommandKind.ImmichRun)) return Invalid("--first is only supported by queue discover and immich run.");
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

        return new CliCommand(kind, configurationPath, null, false, first, all, json, null, kind == CliCommandKind.Process ? remaining[1] : null, force, dryRun, (kind is CliCommandKind.Validate or CliCommandKind.Finalize or CliCommandKind.QueueCancel or CliCommandKind.QueueRetry) && remaining.Count > 1 ? remaining[^1] : null, finalizeReady);
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
