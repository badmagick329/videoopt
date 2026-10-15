using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Queue;

namespace VideoOptimiser.Cli.Features.Queue;

internal static class QueueRegistration
{
    public static IServiceCollection AddQueue(this IServiceCollection services) => services
        .AddSingleton<IQueueDiscovery, QueueDiscovery>()
        .AddSingleton<IJobRetryService, JobRetryService>()
        .AddSingleton<IQueueVerbs, QueueVerbs>()
        .AddSingleton<QueueCommands>();
}
