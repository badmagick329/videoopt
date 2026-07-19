using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Configuration;
using VideoOptimiser.Infrastructure.Diagnostics;

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
            return await ExecuteAsync(host.Services, command, CancellationToken.None);
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

    private const string Usage = """
Usage:
  video-optimiser config init [--config <path>]
  video-optimiser config show [--config <path>]
  video-optimiser config validate [--config <path>]
  video-optimiser doctor [--config <path>] [--json]
""";
}

internal enum CliCommandKind
{
    Invalid,
    Help,
    ConfigInit,
    ConfigShow,
    ConfigValidate,
    Doctor
}

internal sealed record CliCommand(CliCommandKind Kind, string? ConfigurationPath, bool Json, string? Error)
{
    public bool IsValid => Kind != CliCommandKind.Invalid;
    public bool ShowHelp => Kind == CliCommandKind.Help;

    public static CliCommand Parse(string[] args)
    {
        var remaining = new List<string>();
        string? configurationPath = null;
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
                case "--help" or "-h" or "help":
                    return new CliCommand(CliCommandKind.Help, null, false, null);
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
            _ => CliCommandKind.Invalid
        };

        if (kind == CliCommandKind.Invalid)
        {
            return Invalid("Unknown or incomplete command.");
        }

        if (json && kind != CliCommandKind.Doctor)
        {
            return Invalid("--json is only supported by doctor.");
        }

        return new CliCommand(kind, configurationPath, json, null);
    }

    private static CliCommand Invalid(string message) => new(CliCommandKind.Invalid, null, false, message);
}
