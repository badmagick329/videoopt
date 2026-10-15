using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Cli;
using VideoOptimiser.Cli.Features.Calibrate;
using VideoOptimiser.Cli.Features.Cleanup;
using VideoOptimiser.Cli.Features.Config;
using VideoOptimiser.Cli.Features.Doctor;
using VideoOptimiser.Cli.Features.Finalize;
using VideoOptimiser.Cli.Features.ImmichRun;
using VideoOptimiser.Cli.Features.Queue;
using VideoOptimiser.Cli.Features.RemoteSetup;
using VideoOptimiser.Cli.Features.Run;
using VideoOptimiser.Cli.Features.Validate;
using VideoOptimiser.Cli.Features.Worker;
using VideoOptimiser.Domain;
using static CliOutput;

NativeSqliteBootstrapper.Initialize();
return await CliApplication.RunAsync(args);

internal static class CliApplication
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
            CliCommandKind.QueueAddImmich => await services.GetRequiredService<ImmichRunCommand>().AddAsync(configuration, command, cancellationToken),
            CliCommandKind.QueueAddFile => await services.GetRequiredService<QueueCommands>().AddFileAsync(configuration, command, cancellationToken),
            CliCommandKind.Run => await services.GetRequiredService<RunCommand>().RunAsync(configuration, command, cancellationToken),
            CliCommandKind.QueueList => await services.GetRequiredService<QueueCommands>().ListAsync(configuration, command, terminal: false, cancellationToken),
            CliCommandKind.QueueHold => await services.GetRequiredService<QueueCommands>().HoldAsync(configuration, command, cancellationToken),
            CliCommandKind.QueueRelease => await services.GetRequiredService<QueueCommands>().ReleaseAsync(configuration, command, cancellationToken),
            CliCommandKind.QueueNext => await services.GetRequiredService<QueueCommands>().NextAsync(configuration, command, cancellationToken),
            CliCommandKind.QueueDrop => await services.GetRequiredService<QueueCommands>().DropAsync(configuration, command, cancellationToken),
            CliCommandKind.QueueRetry => await services.GetRequiredService<QueueCommands>().RetryAsync(configuration, command, cancellationToken),
            CliCommandKind.Status => await services.GetRequiredService<QueueCommands>().ListAsync(configuration, command, terminal: false, cancellationToken),
            CliCommandKind.History => await services.GetRequiredService<QueueCommands>().ListAsync(configuration, command, terminal: true, cancellationToken),
            CliCommandKind.Validate => await services.GetRequiredService<ValidateCommand>().RunAsync(configuration, command.JobId!, cancellationToken),
            CliCommandKind.Finalize => await services.GetRequiredService<FinalizeCommands>().FinalizeAsync(configuration, command, cancellationToken),
            CliCommandKind.Cleanup => await services.GetRequiredService<CleanupCommand>().RunAsync(configuration, command.DryRun, cancellationToken),
            CliCommandKind.ImmichRun => await services.GetRequiredService<ImmichRunCommand>().RunAsync(configuration, command, cancellationToken),
            CliCommandKind.ImmichFinalize => await services.GetRequiredService<FinalizeCommands>().ImmichFinalizeAsync(configuration, cancellationToken),
            CliCommandKind.Calibrate => await services.GetRequiredService<CalibrateCommand>().RunAsync(configuration, command.Json, cancellationToken),
            _ => (int)ExitCode.InvalidArguments
        };
    }

    private static string GetVersion() => typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";
}
