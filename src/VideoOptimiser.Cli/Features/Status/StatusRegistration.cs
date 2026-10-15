using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Status;

namespace VideoOptimiser.Cli.Features.Status;

internal static class StatusRegistration
{
    public static IServiceCollection AddStatus(this IServiceCollection services) => services
        .AddSingleton<IStatusCard, StatusCard>()
        .AddSingleton<StatusCommand>();
}
