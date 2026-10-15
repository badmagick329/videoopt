using VideoOptimiser.Application;

namespace VideoOptimiser.Cli;

/// <summary>
/// The one place that writes a suggested command, for the CLI (<see cref="CliCommand.Hint"/>) and, through
/// <see cref="ICommandHints"/>, for the application layer. Registered per run with the path the user typed.
/// </summary>
internal sealed class CommandHints(string? configurationPath) : ICommandHints
{
    public string Command(string verb) => Format(verb, configurationPath);

    public static string Format(string verb, string? configurationPath) =>
        $"video-optimiser {verb}{(configurationPath is null ? string.Empty : $" --config \"{configurationPath}\"")}";
}
