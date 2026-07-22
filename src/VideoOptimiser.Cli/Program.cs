using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Configuration;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Processing;
using VideoOptimiser.Infrastructure.Scanning;

return await CliApplication.RunAsync(args);

internal static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();

        try
        {
            var command = CliCommand.Parse(args);
            if (command.ShowHelp)
            {
                Console.WriteLine(Usage);
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
        catch (Exception exception)
        {
            Log.Fatal(exception, "Unhandled CLI failure");
            Console.Error.WriteLine($"Unexpected failure: {exception.Message}");
            return (int)ExitCode.GeneralFailure;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: false);
        builder.Services.AddSingleton<IConfigurationLoader, YamlConfigurationLoader>();
        builder.Services.AddSingleton<IConfigurationTemplateWriter, YamlConfigurationTemplateWriter>();
        builder.Services.AddSingleton<ISettingsValidator, SettingsValidator>();
        builder.Services.AddSingleton<IDatabaseInitializer, SqliteDatabaseInitializer>();
        builder.Services.AddSingleton<IToolVerifier, ProcessToolVerifier>();
        builder.Services.AddSingleton<IDoctorService, DoctorService>();
        builder.Services.AddSingleton<IFileReadinessService, FileReadinessService>();
        builder.Services.AddSingleton<Func<string, IMediaProbe>>(_ => ffprobePath => new FfprobeMediaProbe(ffprobePath));
        builder.Services.AddSingleton<IFileScanner, FileScanner>();
        builder.Services.AddSingleton<Func<string, ICrfSearchClient>>(_ => abAv1Path => new AbAv1CrfSearchClient(abAv1Path));
        builder.Services.AddSingleton<Func<string, IVideoEncoder>>(_ => abAv1Path => new AbAv1VideoEncoder(abAv1Path));
        builder.Services.AddSingleton<IOutputManifestStore, OutputManifestStore>();
        builder.Services.AddSingleton<IFileFingerprintService, FileFingerprintService>();
        builder.Services.AddSingleton<IJobRepository, SqliteJobRepository>();
        builder.Services.AddSingleton<IOutputValidationService, OutputValidationService>();
        builder.Services.AddSingleton<IJobProcessor, JobProcessor>();
        builder.Services.AddSingleton<IQueueService, QueueService>();
        builder.Services.AddSingleton<IFinalizationService, FinalizationService>();
        builder.Services.AddSingleton<ISafeFileInstaller, SafeFileInstaller>();
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
        ConfigureLogging(configuration.Settings, configuration.Path);

        return command.Kind switch
        {
            CliCommandKind.ConfigShow => ShowConfiguration(configuration),
            CliCommandKind.ConfigValidate => ValidateConfiguration(services.GetRequiredService<ISettingsValidator>(), configuration),
            CliCommandKind.Doctor => await RunDoctorAsync(services.GetRequiredService<IDoctorService>(), configuration, command.Json, cancellationToken),
            CliCommandKind.Process => await RunProcessAsync(services, configuration, command, cancellationToken),
            CliCommandKind.QueueDiscover => await RunQueueDiscoverAsync(services.GetRequiredService<IQueueService>(), configuration, command.First, cancellationToken),
            CliCommandKind.QueueRun => await RunQueueRunAsync(services.GetRequiredService<IQueueService>(), configuration, cancellationToken),
            CliCommandKind.QueueList => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: false, command.Json, cancellationToken),
            CliCommandKind.QueueCancel => await RunQueueCancelAsync(services.GetRequiredService<IJobRepository>(), configuration, command, cancellationToken),
            CliCommandKind.Status => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: false, command.Json, cancellationToken),
            CliCommandKind.History => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: true, command.Json, cancellationToken),
            CliCommandKind.Validate => await RunValidateAsync(services, configuration, command.JobId!, cancellationToken),
            CliCommandKind.Finalize => await RunFinalizeAsync(services, configuration, command, cancellationToken),
            _ => (int)ExitCode.InvalidArguments
        };
    }

    private static void ConfigureLogging(AppSettings settings, string configurationPath)
    {
        var level = Enum.TryParse<LogEventLevel>(settings.Logging.Level, ignoreCase: true, out var parsedLevel)
            ? parsedLevel
            : LogEventLevel.Information;
        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.WithProperty("ConfigurationPath", configurationPath);

        if (settings.Logging.Console)
        {
            loggerConfiguration = loggerConfiguration.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
        }

        if (settings.Logging.StructuredFile && !string.IsNullOrWhiteSpace(settings.Logging.Directory))
        {
            Directory.CreateDirectory(settings.Logging.Directory);
            loggerConfiguration = loggerConfiguration.WriteTo.File(
                path: Path.Combine(settings.Logging.Directory, "video-optimiser-.json"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: settings.Logging.RetainDays,
                formatter: new Serilog.Formatting.Compact.CompactJsonFormatter());
        }

        Log.Logger = loggerConfiguration.CreateLogger();
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
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                configurationPath = configuration.Path,
                exitCode = (int)report.ExitCode,
                diagnostics = report.Diagnostics.Select(diagnostic => new
                {
                    category = diagnostic.Category.ToString(),
                    status = diagnostic.Status.ToString(),
                    code = diagnostic.Code,
                    message = diagnostic.Message
                })
            }, JsonOptions));
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

        var sourcePath = Path.GetFullPath(command.ProcessPath!);
        if (command.DryRun)
        {
            Console.WriteLine($"Would create a job for: {sourcePath}");
            Console.WriteLine("Would run CRF search, encode a temporary AV1, then validate it. No source file would be changed.");
            return (int)ExitCode.Success;
        }

        var repository = services.GetRequiredService<IJobRepository>();
        await repository.MarkActiveJobsInterruptedAsync(configuration.Settings.Database.Path, cancellationToken);
        Console.WriteLine($"Processing: {sourcePath}");
        var progress = new InlineProgress<CrfSearchOutput>(update => Console.WriteLine(update.Text));
        var result = await services.GetRequiredService<IJobProcessor>().ProcessAsync(configuration.Settings.Database.Path, sourcePath, configuration.Settings, command.Force, progress, cancellationToken);
        if (result.ExitCode == ExitCode.Success)
        {
            Console.WriteLine(result.Message);
        }
        else
        {
            Console.Error.WriteLine(result.Message);
        }

        return (int)result.ExitCode;
    }

    private static async Task<int> RunQueueDiscoverAsync(IQueueService queue, LoadedConfiguration configuration, bool first, CancellationToken cancellationToken)
    {
        var result = await queue.DiscoverAsync(configuration.Settings.Database.Path, configuration.Settings, first, cancellationToken);
        foreach (var path in result.QueuedPaths) Console.WriteLine($"Queued  {path}");
        Console.WriteLine($"Queued: {result.QueuedPaths.Count}. Already queued: {result.AlreadyQueued}. Issues: {result.Issues}.");
        return result.Issues == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }

    private static async Task<int> RunQueueCancelAsync(IJobRepository repository, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        JobRecord[] jobs = command.All
            ? (await repository.ListAsync(configuration.Settings.Database.Path, terminal: false, cancellationToken)).Where(job => job.Status is JobStatus.Queued or JobStatus.Interrupted).ToArray()
            : Guid.TryParse(command.JobId, out var id) ? (await repository.GetAsync(configuration.Settings.Database.Path, id, cancellationToken) is { } foundJob ? [foundJob] : Array.Empty<JobRecord>()) : Array.Empty<JobRecord>();
        if (jobs.Length == 0) { Console.WriteLine("No cancellable jobs found."); return (int)ExitCode.Success; }
        foreach (var job in jobs)
        {
            if (job.Status is not (JobStatus.Queued or JobStatus.Interrupted)) { Console.Error.WriteLine($"{job.Id:N} is not cancellable."); continue; }
            job.Status = JobStatus.Cancelled;
            job.FailureCategory = "Cancelled";
            job.FailureMessage = "Cancelled by user.";
            job.CompletedUtc = DateTimeOffset.UtcNow;
            await repository.UpdateAsync(configuration.Settings.Database.Path, job, cancellationToken);
            Console.WriteLine($"Cancelled  {job.Id:N}  {Path.GetFileName(job.SourcePath)}");
        }
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunQueueRunAsync(IQueueService queue, LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        Console.WriteLine("Running queued jobs.");
        var progress = new InlineProgress<CrfSearchOutput>(update => Console.WriteLine(update.Text));
        var result = await queue.RunAsync(configuration.Settings.Database.Path, configuration.Settings, progress, cancellationToken);
        Console.WriteLine($"Ready to finalize: {result.ReadyToFinalize}. Failed: {result.Failed}.");
        return (int)result.ExitCode;
    }

    private static async Task<int> RunJobListAsync(IJobRepository repository, LoadedConfiguration configuration, bool terminal, bool json, CancellationToken cancellationToken)
    {
        var jobs = await repository.ListAsync(configuration.Settings.Database.Path, terminal, cancellationToken);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(jobs.Select(job => new
            {
                id = job.Id.ToString("N"),
                status = job.Status.ToString(),
                sourcePath = job.SourcePath,
                crf = job.Crf,
                outputPath = job.OutputPath,
                percentageSaved = job.PercentageSaved,
                failureCategory = job.FailureCategory,
                failureMessage = job.FailureMessage,
                updatedUtc = job.UpdatedUtc
            }), JsonOptions));
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
                Console.WriteLine($"{job.Id:N}  {job.Status,-16} {Path.GetFileName(job.SourcePath)}{(job.Crf is null ? string.Empty : $" CRF {job.Crf}")}{savings}{suffix}");
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

        var ready = (await repository.ListAsync(configuration.Settings.Database.Path, terminal: false, cancellationToken)).Where(job => job.Status == JobStatus.ReadyToFinalize).ToArray();
        if (ready.Length == 0) { Console.WriteLine("No jobs are ready to finalize."); return (int)ExitCode.Success; }
        foreach (var job in ready) Console.WriteLine($"{job.Id:N}  {Path.GetFileName(job.SourcePath)}");
        Console.Write("Finalize these jobs? [y/N] ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Finalization cancelled."); return (int)ExitCode.Success; }
        var failures = 0;
        foreach (var job in ready)
        {
            var result = await finalizer.FinalizeAsync(configuration.Settings.Database.Path, job.Id, configuration.Settings, cancellationToken);
            (result.ExitCode == ExitCode.Success ? Console.Out : Console.Error).WriteLine($"{job.Id:N}: {result.Message}");
            if (result.ExitCode != ExitCode.Success) failures++;
        }
        return failures == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }

    private const string Usage = """
Usage:
  video-optimiser config init [--config <path>]
  video-optimiser config show [--config <path>]
  video-optimiser config validate [--config <path>]
  video-optimiser doctor [--config <path>] [--json]
  video-optimiser process <file> [--config <path>] [--force] [--dry-run]
  video-optimiser queue discover [--first] [--config <path>]
  video-optimiser queue run [--config <path>]
  video-optimiser queue list [--config <path>] [--json]
  video-optimiser queue cancel <job-id> [--config <path>]
  video-optimiser queue cancel --all [--config <path>]
  video-optimiser status [--config <path>] [--json]
  video-optimiser history [--config <path>] [--json]
  video-optimiser validate <job-id> [--config <path>]
  video-optimiser finalize <job-id> [--config <path>]
  video-optimiser finalize --ready [--config <path>]
""";
}

internal enum CliCommandKind
{
    Invalid,
    Help,
    ConfigInit,
    ConfigShow,
    ConfigValidate,
    Doctor,
    Process,
    QueueDiscover,
    QueueRun,
    QueueList,
    QueueCancel,
    Status,
    History,
    Validate,
    Finalize
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
            ["doctor"] => CliCommandKind.Doctor,
            ["process", _] => CliCommandKind.Process,
            ["queue", "discover"] => CliCommandKind.QueueDiscover,
            ["queue", "run"] => CliCommandKind.QueueRun,
            ["queue", "list"] => CliCommandKind.QueueList,
            ["queue", "cancel"] or ["queue", "cancel", _] => CliCommandKind.QueueCancel,
            ["status"] => CliCommandKind.Status,
            ["history"] => CliCommandKind.History,
            ["validate", _] => CliCommandKind.Validate,
            ["finalize"] or ["finalize", _] => CliCommandKind.Finalize,
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

        if (first && kind != CliCommandKind.QueueDiscover) return Invalid("--first is only supported by queue discover.");
        if (all && kind != CliCommandKind.QueueCancel) return Invalid("--all is only supported by queue cancel.");

        if (force && kind != CliCommandKind.Process)
        {
            return Invalid("--force is only supported by process.");
        }

        if (dryRun && kind != CliCommandKind.Process) return Invalid("--dry-run is only supported by process.");
        if (finalizeReady && kind != CliCommandKind.Finalize) return Invalid("--ready is only supported by finalize.");
        if (finalizeReady && remaining.Count != 1) return Invalid("finalize --ready does not take a job ID.");
        if (kind == CliCommandKind.QueueCancel && !all && remaining.Count != 3) return Invalid("queue cancel requires a job ID or --all.");

        return new CliCommand(kind, configurationPath, null, false, first, all, json, null, kind == CliCommandKind.Process ? remaining[1] : null, force, dryRun, (kind is CliCommandKind.Validate or CliCommandKind.Finalize or CliCommandKind.QueueCancel) && remaining.Count > 1 ? remaining[^1] : null, finalizeReady);
    }

    private static CliCommand Invalid(string message) => new(CliCommandKind.Invalid, null, null, false, false, false, false, message);
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
