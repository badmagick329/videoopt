using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.ImmichRun;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.Cli.Features.ImmichRun;

internal static class ImmichRunRegistration
{
    // Lambdas, because the container cannot fill the constructors' optional clock and batch size; leaving them out keeps
    // their defaults.
    public static IServiceCollection AddImmichRun(this IServiceCollection services) => services
        .AddSingleton<IImmichDiscoveryService>(provider => new ImmichDiscoveryService(provider.GetRequiredService<IImmichClientFactory>(), provider.GetRequiredService<IJobRepository>(), provider.GetRequiredService<Func<string, IMediaProbe>>(), provider.GetRequiredService<IFileFingerprintService>()))
        .AddSingleton<IImmichReuploadGuard>(provider => new ImmichReuploadGuard(provider.GetRequiredService<IImmichClientFactory>(), provider.GetRequiredService<IJobRepository>()))
        .AddSingleton<IImmichRunService, ImmichRunService>()
        .AddSingleton<ImmichRunCommand>();
}
