using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.RemoteSetup;
using VideoOptimiser.Infrastructure.Features.RemoteSetup;

namespace VideoOptimiser.Cli.Features.RemoteSetup;

internal static class RemoteSetupRegistration
{
    public static IServiceCollection AddRemoteSetup(this IServiceCollection services) => services
        .AddSingleton<IRemoteSetupService, RemoteSetupService>()
        .AddSingleton<RemoteSetupCommand>();
}
