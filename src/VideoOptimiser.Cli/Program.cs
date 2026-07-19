using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Configuration;
using VideoOptimiser.Infrastructure.Diagnostics;
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
            CliCommandKind.Encode => await RunEncodeAsync(services, configuration, command, cancellationToken),
            CliCommandKind.Validate => await RunValidateAsync(services, configuration, command.OutputPath!, cancellationToken),
            CliCommandKind.Finalize => await RunFinalizeAsync(services, configuration, command.OutputPath!, cancellationToken),
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

        IProgress<ScanProgress>? progress = command.Json
            ? null
            : new InlineProgress<ScanProgress>(update => Console.WriteLine($"{update.Stage,-20} {update.Path} — {update.Message}"));
        var report = await scanner.ScanAsync(
            roots,
            configuration.Settings,
            useImmediateStabilityCheck: true,
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
            foreach (var item in report.Items)
            {
                Console.WriteLine($"{item.Status,-20} {item.Path} — {item.Reason}");
            }

            foreach (var issue in report.Issues)
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
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"Source file does not exist: {sourcePath}");
            return (int)ExitCode.ProcessingFailure;
        }

        if (!command.Force && !configuration.Settings.Watch.Roots.Any(root => IsWithinRoot(sourcePath, root.Path)))
        {
            Console.Error.WriteLine("Source file is outside configured watch roots. Use --force to bypass this check.");
            return (int)ExitCode.ProcessingFailure;
        }

        var info = new FileInfo(sourcePath);
        if (!HumanReadableValues.TryParseSize(configuration.Settings.Processing.MinimumFileSize, out var minimumSize) || info.Length < minimumSize)
        {
            Console.Error.WriteLine("Source file is below processing.minimumFileSize. Use --force to bypass this check.");
            return (int)ExitCode.NoEligibleFiles;
        }

        var stability = await services.GetRequiredService<IFileStabilityService>().WaitUntilStableAsync(
            sourcePath,
            configuration.Settings.Watch.Stability,
            requireRepeatedObservations: false,
            cancellationToken: cancellationToken);
        if (!stability.IsStable)
        {
            Console.Error.WriteLine($"Source is not ready: {stability.Reason}");
            return (int)ExitCode.ProcessingFailure;
        }

        var media = await services.GetRequiredService<Func<string, IMediaProbe>>()(configuration.Settings.Tools.FfprobePath).ProbeAsync(sourcePath, cancellationToken);
        if (!command.Force && !configuration.Settings.Eligibility.RequiredVideoCodecs.Contains(media.PrimaryVideoCodec, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Source codec is {media.PrimaryVideoCodec}; expected {string.Join(", ", configuration.Settings.Eligibility.RequiredVideoCodecs)}. Use --force to bypass this check.");
            return (int)ExitCode.NoEligibleFiles;
        }

        var client = services.GetRequiredService<Func<string, ICrfSearchClient>>()(configuration.Settings.Tools.AbAv1Path);
        if (command.DryRun)
        {
            Console.WriteLine("Would run:");
            Console.WriteLine(configuration.Settings.Tools.AbAv1Path + " " + string.Join(" ", client.BuildArguments(sourcePath, configuration.Settings.Quality).Select(Quote)));
            return (int)ExitCode.Success;
        }

        Console.WriteLine($"CRF search: {sourcePath}");
        var progress = new InlineProgress<CrfSearchOutput>(update => Console.WriteLine(update.Text));
        var result = await client.SearchAsync(sourcePath, configuration.Settings.Quality, progress, cancellationToken);
        Console.WriteLine($"Selected CRF: {result.Crf} ({result.Duration:g})");
        return (int)ExitCode.Success;
    }

    private static bool IsWithinRoot(string sourcePath, string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
        return sourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

    private static async Task<int> RunEncodeAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(command.ProcessPath!);
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"Source file does not exist: {sourcePath}");
            return (int)ExitCode.ProcessingFailure;
        }

        if (command.Crf is null or < 1 or > 63)
        {
            Console.Error.WriteLine("--crf must be between 1 and 63.");
            return (int)ExitCode.InvalidArguments;
        }

        var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        var outputDirectory = Path.Combine(sourceDirectory, ".video-optimiser");
        var outputPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(sourcePath)}.{Guid.NewGuid():N}.encoding{Path.GetExtension(sourcePath)}");
        var fingerprint = await services.GetRequiredService<IFileFingerprintService>().CreateAsync(sourcePath, cancellationToken);
        var encoder = services.GetRequiredService<Func<string, IVideoEncoder>>()(configuration.Settings.Tools.AbAv1Path);
        if (command.DryRun)
        {
            Console.WriteLine("Would write temporary output:");
            Console.WriteLine(outputPath);
            Console.WriteLine(configuration.Settings.Tools.AbAv1Path + " " + string.Join(" ", encoder.BuildArguments(sourcePath, outputPath, command.Crf.Value, configuration.Settings.Quality).Select(Quote)));
            return (int)ExitCode.Success;
        }

        Directory.CreateDirectory(outputDirectory);
        Console.WriteLine($"Encoding to temporary file: {outputPath}");
        var progress = new InlineProgress<CrfSearchOutput>(update => Console.WriteLine(update.Text));
        var result = await encoder.EncodeAsync(sourcePath, outputPath, command.Crf.Value, configuration.Settings.Quality, progress, cancellationToken);
        await services.GetRequiredService<IOutputManifestStore>().SaveAsync(new OutputManifest { SourcePath = sourcePath, SourceFingerprint = fingerprint, OutputPath = outputPath, Crf = command.Crf.Value, CreatedUtc = DateTimeOffset.UtcNow }, cancellationToken);
        Console.WriteLine($"Temporary AV1 file created: {result.OutputPath} ({result.Duration:g})");
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunValidateAsync(IServiceProvider services, LoadedConfiguration configuration, string outputPath, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IOutputManifestStore>();
        var manifest = await store.LoadAsync(Path.GetFullPath(outputPath), cancellationToken);
        var failures = new List<string>();
        if (!File.Exists(manifest.SourcePath) || !File.Exists(manifest.OutputPath)) failures.Add("Source or temporary output is missing.");
        var source = failures.Count == 0 ? await services.GetRequiredService<Func<string, IMediaProbe>>()(configuration.Settings.Tools.FfprobePath).ProbeAsync(manifest.SourcePath, cancellationToken) : null;
        var output = failures.Count == 0 ? await services.GetRequiredService<Func<string, IMediaProbe>>()(configuration.Settings.Tools.FfprobePath).ProbeAsync(manifest.OutputPath, cancellationToken) : null;
        if (output is not null && output.PrimaryVideoCodec != "av1") failures.Add("Output video is not AV1.");
        if (source is not null && output is not null)
        {
            if (Math.Abs((source.DurationSeconds ?? 0) - (output.DurationSeconds ?? 0)) > configuration.Settings.Validation.DurationToleranceSeconds) failures.Add("Output duration differs from source.");
            if (source.AudioStreamCount != output.AudioStreamCount || source.SubtitleStreamCount != output.SubtitleStreamCount) failures.Add("Output stream counts differ from source.");
        }
        var sourceSize = new FileInfo(manifest.SourcePath).Length;
        var outputSize = new FileInfo(manifest.OutputPath).Length;
        var saved = (decimal)(sourceSize - outputSize) / sourceSize * 100;
        if (configuration.Settings.Savings.RequireSmallerOutput && outputSize >= sourceSize || saved < configuration.Settings.Savings.MinimumPercentageSaved) failures.Add("Output does not meet savings policy.");
        manifest.Validation = new ValidationReport { Passed = failures.Count == 0, Failures = failures, SourceSizeBytes = sourceSize, OutputSizeBytes = outputSize, PercentageSaved = saved, ValidatedUtc = DateTimeOffset.UtcNow };
        await store.SaveAsync(manifest, cancellationToken);
        Console.WriteLine(manifest.Validation.Passed ? $"Validation passed: saved {saved:F1}%" : "Validation failed: " + string.Join("; ", failures));
        return manifest.Validation.Passed ? (int)ExitCode.Success : (int)ExitCode.ValidationFailure;
    }

    private static async Task<int> RunFinalizeAsync(IServiceProvider services, LoadedConfiguration configuration, string outputPath, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IOutputManifestStore>();
        var manifest = await store.LoadAsync(Path.GetFullPath(outputPath), cancellationToken);
        if (manifest.Validation?.Passed != true) { Console.Error.WriteLine("Run validate successfully before finalize."); return (int)ExitCode.ValidationFailure; }
        if (!configuration.Settings.Original.Action.Equals("delete", StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("finalize currently requires original.action: delete."); return (int)ExitCode.InvalidConfiguration; }
        if (await services.GetRequiredService<IFileFingerprintService>().CreateAsync(manifest.SourcePath, cancellationToken) != manifest.SourceFingerprint) { Console.Error.WriteLine("Source changed after encoding."); return (int)ExitCode.ProcessingFailure; }
        await services.GetRequiredService<ISafeFileInstaller>().InstallAsync(manifest.SourcePath, manifest.OutputPath, cancellationToken);
        Console.WriteLine("Finalization complete. Original deleted.");
        return (int)ExitCode.Success;
    }

    private const string Usage = """
Usage:
  video-optimiser config init [--config <path>]
  video-optimiser config show [--config <path>]
  video-optimiser config validate [--config <path>]
  video-optimiser doctor [--config <path>] [--json]
  video-optimiser scan [--config <path>] [--path <folder>] [--recursive] [--json]
  video-optimiser process <file> [--config <path>] [--force] [--dry-run]
  video-optimiser encode <file> --crf <number> [--config <path>] [--dry-run]
  video-optimiser validate <temporary-output> [--config <path>]
  video-optimiser finalize <temporary-output> [--config <path>]
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
    Encode, Validate, Finalize
}

internal sealed record CliCommand(CliCommandKind Kind, string? ConfigurationPath, string? ScanPath, bool Recursive, bool Json, string? Error, string? ProcessPath = null, bool Force = false, bool DryRun = false, int? Crf = null, string? OutputPath = null)
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
        int? crf = null;
        var recursive = false;
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
                case "--crf" when index + 1 < args.Length && int.TryParse(args[index + 1], out var parsedCrf):
                    crf = parsedCrf;
                    index++;
                    break;
                case "--crf":
                    return Invalid("--crf requires an integer.");
                case "--help" or "-h" or "help":
                    return new CliCommand(CliCommandKind.Help, null, null, false, false, null);
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
            ["encode", _] => CliCommandKind.Encode,
            ["validate", _] => CliCommandKind.Validate,
            ["finalize", _] => CliCommandKind.Finalize,
            _ => CliCommandKind.Invalid
        };

        if (kind == CliCommandKind.Invalid)
        {
            return Invalid("Unknown or incomplete command.");
        }

        if (json && kind is not (CliCommandKind.Doctor or CliCommandKind.Scan))
        {
            return Invalid("--json is only supported by doctor and scan.");
        }

        if ((scanPath is not null || recursive) && kind != CliCommandKind.Scan)
        {
            return Invalid("--path and --recursive are only supported by scan.");
        }

        if (force && kind != CliCommandKind.Process)
        {
            return Invalid("--force is only supported by process.");
        }

        if (dryRun && kind is not (CliCommandKind.Process or CliCommandKind.Encode)) return Invalid("--dry-run is only supported by process and encode.");
        if (crf is not null && kind != CliCommandKind.Encode) return Invalid("--crf is only supported by encode.");

        return new CliCommand(kind, configurationPath, scanPath, recursive, json, null, kind is CliCommandKind.Process or CliCommandKind.Encode ? remaining[1] : null, force, dryRun, crf, kind is CliCommandKind.Validate or CliCommandKind.Finalize ? remaining[1] : null);
    }

    private static CliCommand Invalid(string message) => new(CliCommandKind.Invalid, null, null, false, false, message);
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
