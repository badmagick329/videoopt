using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Infrastructure.Features.Worker;

namespace VideoOptimiser.Cli.Features.Worker;

internal static class WorkerRegistration
{
    public static IServiceCollection AddWorker(this IServiceCollection services) => services
        .AddSingleton<IRemoteWorkerLifecycle, HetznerRemoteWorkerLifecycle>()
        .AddSingleton<WorkerCommands>();
}
