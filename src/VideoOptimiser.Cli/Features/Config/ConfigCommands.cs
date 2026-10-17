using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Config;
using VideoOptimiser.Domain;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Config;

/// <summary><c>config init</c>, <c>config show</c> and <c>config validate</c>.</summary>
internal sealed class ConfigCommands(IConfigurationTemplateWriter writer, ISettingsValidator validator, IConsoleInput input)
{
    /// <summary>Runs before any configuration is loaded: the file it writes may be the first one.</summary>
    public async Task<int> InitAsync(CliCommand command, CancellationToken cancellationToken)
    {
        try
        {
            // Refuse before asking, so nobody answers a question for a file that will not be written.
            var target = writer.GetDestination(command.ConfigurationPath);
            if (File.Exists(target)) throw IConfigurationTemplateWriter.RefuseOverwrite(target);
            var quality = ConfigInitQuestion.Choose(command.Quality, input.IsInteractive, CliConsole.In, CliConsole.Out);
            var destination = await writer.WriteAsync(command.ConfigurationPath, quality, cancellationToken);
            CliConsole.WriteLine($"Created editable configuration template: {destination}");
            CliConsole.WriteLine($"Add watch.roots, then run '{command.Hint("config validate")}'.");
            return (int)ExitCode.Success;
        }
        catch (InvalidOperationException exception)
        {
            WriteError(exception.Message);
            return (int)ExitCode.GeneralFailure;
        }
    }

    public static int Show(LoadedConfiguration configuration)
    {
        CliConsole.WriteLine($"Configuration path: {configuration.Path}");
        CliConsole.WriteLine();
        CliConsole.Write(configuration.RawYaml ?? "# Configuration content is not available.\n");
        return (int)ExitCode.Success;
    }

    public int Validate(LoadedConfiguration configuration)
    {
        var diagnostics = validator.Validate(configuration.Settings);
        if (diagnostics.Count == 0)
        {
            CliConsole.WriteLine($"Valid configuration: {configuration.Path}");
            return (int)ExitCode.Success;
        }

        foreach (var diagnostic in diagnostics)
        {
            WriteError($"[{diagnostic.Code}] {diagnostic.Message}");
        }

        return (int)ExitCode.InvalidConfiguration;
    }
}
