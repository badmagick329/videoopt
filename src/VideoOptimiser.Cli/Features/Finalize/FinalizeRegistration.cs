using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Infrastructure.Features.Finalize;

namespace VideoOptimiser.Cli.Features.Finalize;

internal static class FinalizeRegistration
{
    public static IServiceCollection AddFinalize(this IServiceCollection services) => services
        .AddSingleton<ImmichFinalizer>()
        .AddSingleton<IFinalizationService, FinalizationService>()
        .AddSingleton<ISafeFileInstaller, SafeFileInstaller>()
        .AddSingleton<FinalizeCommands>();
}
