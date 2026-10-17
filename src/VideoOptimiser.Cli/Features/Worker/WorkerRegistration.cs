using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Infrastructure.Features.Worker;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.Cli.Features.Worker;

internal static class WorkerRegistration
{
    public static IServiceCollection AddWorker(this IServiceCollection services) => services
        .AddSingleton<SqliteWorkerBillStore>()
        .AddSingleton<IRemoteWorkerLifecycle, HetznerRemoteWorkerLifecycle>()
        .AddSingleton<WorkerCommands>();
}
