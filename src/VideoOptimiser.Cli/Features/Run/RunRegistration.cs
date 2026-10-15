using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Infrastructure.Features.Run;

namespace VideoOptimiser.Cli.Features.Run;

internal static class RunRegistration
{
    // RunStop and RunWorkerState hold one command's state; a host serves one command, so singletons are enough.
    public static IServiceCollection AddRun(this IServiceCollection services) => services
        .AddSingleton<IQueueService, QueueService>()
        .AddSingleton<IProcessingSessionFactory, ProcessingSessionFactory>()
        .AddSingleton<IJobProcessor, JobProcessor>()
        .AddSingleton<RunStop>()
        .AddSingleton<RunWorkerState>()
        .AddSingleton<RunWorkerEnd>()
        .AddSingleton<RunConversion>()
        .AddSingleton<RunCommand>();
}
