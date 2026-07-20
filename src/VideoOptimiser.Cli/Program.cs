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
        builder.Services.AddSingleton<IFileStabilityService, FileStabilityService>();
        builder.Services.AddSingleton<Func<string, IMediaProbe>>(_ => ffprobePath => new FfprobeMediaProbe(ffprobePath));
        builder.Services.AddSingleton<IFileScanner, FileScanner>();
        builder.Services.AddSingleton<Func<string, ICrfSearchClient>>(_ => abAv1Path => new AbAv1CrfSearchClient(abAv1Path));
        builder.Services.AddSingleton<Func<string, IVideoEncoder>>(_ => abAv1Path => new AbAv1VideoEncoder(abAv1Path));
        builder.Services.AddSingleton<IOutputManifestStore, OutputManifestStore>();
        builder.Services.AddSingleton<IFileFingerprintService, FileFingerprintService>();
        builder.Services.AddSingleton<IJobRepository, SqliteJobRepository>();
        builder.Services.AddSingleton<IOutputValidationService, OutputValidationService>();
        builder.Services.AddSingleton<IJobProcessor, JobProcessor>();
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
                Console.WriteLine("Add watch.roots and original.archiveDirectory, then run 'video-optimiser config validate'.");
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
            CliCommandKind.Scan => await RunScanAsync(services.GetRequiredService<IFileScanner>(), configuration, command, cancellationToken),
            CliCommandKind.Process => await RunProcessAsync(services, configuration, command, cancellationToken),
            CliCommandKind.Status => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: false, command.Json, cancellationToken),
            CliCommandKind.History => await RunJobListAsync(services.GetRequiredService<IJobRepository>(), configuration, terminal: true, command.Json, cancellationToken),
            CliCommandKind.Validate => await RunValidateAsync(services, configuration, command.JobId!, cancellationToken),
            CliCommandKind.Finalize => await RunFinalizeAsync(services, configuration, command.JobId!, cancellationToken),
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

    private static async Task<int> RunScanAsync(IFileScanner scanner, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var roots = string.IsNullOrWhiteSpace(command.ScanPath)
            ? configuration.Settings.Watch.Roots
            : [new WatchRootSettings { Path = Path.GetFullPath(command.ScanPath), Recursive = command.Recursive }];
        if (string.IsNullOrWhiteSpace(command.ScanPath) && command.Recursive)
        {
            roots = roots.Select(root => new WatchRootSettings { Path = root.Path, Recursive = true }).ToList();
        }

        IProgress<ScanProgress>? progress = command.Json || !command.All
            ? null
            : new InlineProgress<ScanProgress>(update => Console.WriteLine($"{update.Stage,-20} {update.Path} — {update.Message}"));
        var report = await scanner.ScanAsync(
            roots,
            configuration.Settings,
            useImmediateStabilityCheck: true,
            stopAfterFirstEligible: command.First,
            progress: progress,
            cancellationToken: cancellationToken);
        if (command.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                eligibleCount = report.EligibleCount,
                items = report.Items.Select(item => new
                {
                    path = item.Path,
                    status = item.Status.ToString(),
                    reason = item.Reason,
                    sizeBytes = item.SizeBytes,
                    primaryVideoCodec = item.MediaInfo?.PrimaryVideoCodec
                }),
                issues = report.Issues.Select(issue => new { path = issue.Path, message = issue.Message })
            }, JsonOptions));
        }
        else
        {
            foreach (var item in report.Items.Where(item => command.All || item.Status == ScanItemStatus.Eligible))
            {
                Console.WriteLine($"{item.Status,-20} {item.Path} — {item.Reason}");
            }

            foreach (var issue in report.Issues.Where(_ => command.All))
            {
                Console.Error.WriteLine($"Issue                {issue.Path} — {issue.Message}");
            }

            Console.WriteLine($"Eligible files: {report.EligibleCount}");
        }

        if (report.Items.Any(item => item.Status == ScanItemStatus.ProbeFailed) || report.Issues.Count > 0)
        {
            return (int)ExitCode.GeneralFailure;
        }

        return report.EligibleCount > 0 ? (int)ExitCode.Success : (int)ExitCode.NoEligibleFiles;
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

    private static async Task<int> RunFinalizeAsync(IServiceProvider services, LoadedConfiguration configuration, string jobId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(jobId, out var id)) { Console.Error.WriteLine("Job ID is invalid."); return (int)ExitCode.InvalidArguments; }
        var repository = services.GetRequiredService<IJobRepository>();
        var job = await repository.GetAsync(configuration.Settings.Database.Path, id, cancellationToken);
        if (job is null) { Console.Error.WriteLine("Job was not found."); return (int)ExitCode.ProcessingFailure; }
        if (job.Status != JobStatus.ReadyToFinalize || job.OutputPath is null) { Console.Error.WriteLine("Job is not ready to finalize."); return (int)ExitCode.ValidationFailure; }
        var store = services.GetRequiredService<IOutputManifestStore>();
        var manifest = await store.LoadAsync(job.OutputPath, cancellationToken);
        if (manifest.Validation?.Passed != true) { Console.Error.WriteLine("Job validation did not pass."); return (int)ExitCode.ValidationFailure; }
        if (!configuration.Settings.Original.Action.Equals("delete", StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("finalize currently requires original.action: delete."); return (int)ExitCode.InvalidConfiguration; }
        job.Status = JobStatus.Finalizing;
        await repository.UpdateAsync(configuration.Settings.Database.Path, job, cancellationToken);
        try
        {
            if (await services.GetRequiredService<IFileFingerprintService>().CreateAsync(manifest.SourcePath, cancellationToken) != manifest.SourceFingerprint) throw new InvalidOperationException("Source changed after encoding.");
            await services.GetRequiredService<ISafeFileInstaller>().InstallAsync(manifest.SourcePath, manifest.OutputPath, cancellationToken);
            job.Status = JobStatus.Completed;
            job.CompletedUtc = DateTimeOffset.UtcNow;
            await repository.UpdateAsync(configuration.Settings.Database.Path, job, cancellationToken);
            Console.WriteLine("Finalization complete. Original deleted.");
            return (int)ExitCode.Success;
        }
        catch (Exception exception)
        {
            job.Status = JobStatus.Failed;
            job.FailureCategory = "FinalizationFailed";
            job.FailureMessage = exception.Message;
            job.CompletedUtc = DateTimeOffset.UtcNow;
            await repository.UpdateAsync(configuration.Settings.Database.Path, job, cancellationToken);
            Console.Error.WriteLine(exception.Message);
            return (int)ExitCode.FinalisationFailure;
        }
    }

    private const string Usage = """
Usage:
  video-optimiser config init [--config <path>]
  video-optimiser config show [--config <path>]
  video-optimiser config validate [--config <path>]
  video-optimiser doctor [--config <path>] [--json]
  video-optimiser scan [--config <path>] [--path <folder>] [--recursive] [--first] [--all] [--json]
  video-optimiser process <file> [--config <path>] [--force] [--dry-run]
  video-optimiser status [--config <path>] [--json]
  video-optimiser history [--config <path>] [--json]
  video-optimiser validate <job-id> [--config <path>]
  video-optimiser finalize <job-id> [--config <path>]
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
    Scan,
    Process,
    Status,
    History,
    Validate,
    Finalize
}

internal sealed record CliCommand(CliCommandKind Kind, string? ConfigurationPath, string? ScanPath, bool Recursive, bool First, bool All, bool Json, string? Error, string? ProcessPath = null, bool Force = false, bool DryRun = false, string? JobId = null)
{
    public bool IsValid => Kind != CliCommandKind.Invalid;
    public bool ShowHelp => Kind == CliCommandKind.Help;

    public static CliCommand Parse(string[] args)
    {
        var remaining = new List<string>();
        string? configurationPath = null;
        string? scanPath = null;
        var force = false;
        var dryRun = false;
        var recursive = false;
        var first = false;
        var all = false;
        var json = false;

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
                case "--path" when index + 1 < args.Length:
                    scanPath = args[++index];
                    break;
                case "--path":
                    return Invalid("--path requires a folder.");
                case "--recursive":
                    recursive = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--force":
                    force = true;
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
            ["scan"] => CliCommandKind.Scan,
            ["process", _] => CliCommandKind.Process,
            ["status"] => CliCommandKind.Status,
            ["history"] => CliCommandKind.History,
            ["validate", _] => CliCommandKind.Validate,
            ["finalize", _] => CliCommandKind.Finalize,
            _ => CliCommandKind.Invalid
        };

        if (kind == CliCommandKind.Invalid)
        {
            return Invalid("Unknown or incomplete command.");
        }

        if (json && kind is not (CliCommandKind.Doctor or CliCommandKind.Scan or CliCommandKind.Status or CliCommandKind.History))
        {
            return Invalid("--json is only supported by doctor, scan, status, and history.");
        }

        if ((scanPath is not null || recursive || first || all) && kind != CliCommandKind.Scan)
        {
            return Invalid("--path, --recursive, --first, and --all are only supported by scan.");
        }

        if (first && all)
        {
            return Invalid("--first and --all cannot be used together.");
        }

        if (force && kind != CliCommandKind.Process)
        {
            return Invalid("--force is only supported by process.");
        }

        if (dryRun && kind != CliCommandKind.Process) return Invalid("--dry-run is only supported by process.");

        return new CliCommand(kind, configurationPath, scanPath, recursive, first, all, json, null, kind == CliCommandKind.Process ? remaining[1] : null, force, dryRun, kind is CliCommandKind.Validate or CliCommandKind.Finalize ? remaining[1] : null);
    }

    private static CliCommand Invalid(string message) => new(CliCommandKind.Invalid, null, null, false, false, false, false, message);
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
