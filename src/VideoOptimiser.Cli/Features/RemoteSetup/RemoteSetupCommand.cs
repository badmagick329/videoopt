using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.RemoteSetup;

namespace VideoOptimiser.Cli.Features.RemoteSetup;

/// <summary><c>remote setup</c>: the one-time Hetzner setup. The service asks its questions through <see cref="ConsolePrompts.ConfirmAsync"/>.</summary>
internal sealed class RemoteSetupCommand(IRemoteSetupService setup)
{
    public async Task<int> RunAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var result = await setup.RunAsync(configuration, ConsolePrompts.ConfirmAsync, new InlineProgress<string>(Console.WriteLine), new InlineProgress<string>(CliOutput.WriteError), cancellationToken);
        // The Next line is built here because only the CLI knows whether --config was typed, which the hint then repeats.
        if (result.NextVerb is { } verb) Console.WriteLine($"Next: run '{command.Hint(verb)}'.");
        // The service listed the problems; this says what to do about them, as the suggested command would fail the same way.
        else if (result.ConfigStillInvalid) CliOutput.WriteError($"The configuration still has the problems above. Fix them, then run '{command.Hint("config validate")}'.");
        return (int)result.ExitCode;
    }
}
