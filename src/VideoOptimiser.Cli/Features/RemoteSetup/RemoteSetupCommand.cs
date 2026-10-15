using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.RemoteSetup;

namespace VideoOptimiser.Cli.Features.RemoteSetup;

/// <summary><c>remote setup</c>: the one-time Hetzner setup. The service asks its questions through <see cref="ConsolePrompts.ConfirmAsync"/>.</summary>
internal sealed class RemoteSetupCommand(IRemoteSetupService setup)
{
    public async Task<int> RunAsync(LoadedConfiguration configuration, CancellationToken cancellationToken) =>
        (int)await setup.RunAsync(configuration, ConsolePrompts.ConfirmAsync, new InlineProgress<string>(Console.WriteLine), cancellationToken);
}
