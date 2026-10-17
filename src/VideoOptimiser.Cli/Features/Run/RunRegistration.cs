using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Infrastructure.Features.Run;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.Cli.Features.Run;

internal static class RunRegistration
{
    // RunStop, RunWorkerState, RunFlow and the event file's observer hold one command's state; a host serves one command, so
    // singletons are enough.
    public static IServiceCollection AddRun(this IServiceCollection services) => services
        .AddSingleton<IQueueService, QueueService>()
        .AddSingleton<IProcessingSessionFactory, ProcessingSessionFactory>()
        .AddSingleton<IJobProcessor, JobProcessor>()
        .AddSingleton<RunStop>()
        .AddSingleton<RunWorkerState>()
        .AddSingleton<RunFlow>()
        .AddSingleton<FailedValidationRecheck>()
        .AddSingleton<IRunRequestStore, SqliteRunRequestStore>()
        .AddSingleton<IRunRecordStore, SqliteRunRecordStore>()
        .AddSingleton<IRunEventLog, RunEventFile>()
        .AddSingleton<IKeepAwake, WindowsKeepAwake>()
        .AddSingleton<IDetachedLauncher, DetachedLauncher>()
        .AddSingleton<EventLogRunObserver>()
        .AddSingleton<RunStart>()
        .AddSingleton<RunRequestPoll>(provider => new RunRequestPoll(provider.GetRequiredService<IRunRequestStore>()))
        .AddSingleton<BackgroundRun>()
        .AddSingleton<BackgroundLaunch>(provider => new BackgroundLaunch(provider.GetRequiredService<IDatabaseLocks>(), provider.GetRequiredService<IDetachedLauncher>(), provider.GetRequiredService<TimeProvider>()))
        .AddSingleton<StopRunRequest>()
        .AddSingleton<RunWorkerEnd>()
        .AddSingleton<RunConversion>()
        .AddSingleton<RunCommand>()
        .AddSingleton<StopRunCommand>()
        .AddSingleton<BackgroundCommands>();
}
