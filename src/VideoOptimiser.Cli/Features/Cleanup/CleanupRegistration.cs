using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Cleanup;

namespace VideoOptimiser.Cli.Features.Cleanup;

internal static class CleanupRegistration
{
    public static IServiceCollection AddCleanup(this IServiceCollection services) => services
        .AddSingleton<ITemporaryOutputCleanupService, TemporaryOutputCleanupService>()
        .AddSingleton<CleanupCommand>();
}
