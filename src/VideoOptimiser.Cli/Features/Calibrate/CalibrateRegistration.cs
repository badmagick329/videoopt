using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.Calibrate;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Infrastructure.Features.Calibrate;

namespace VideoOptimiser.Cli.Features.Calibrate;

internal static class CalibrateRegistration
{
    // calibrate promises to write nothing, so it gets its own read-only store and a probe cache that never saves.
    public static IServiceCollection AddCalibrate(this IServiceCollection services) => services
        .AddSingleton<ICalibrationService>(provider => ReadOnlyCalibration.Create(
            provider.GetRequiredService<IFileReadinessService>(), provider.GetRequiredService<Func<string, IMediaProbe>>(), provider.GetRequiredService<IImmichClientFactory>()))
        .AddSingleton<CalibrateCommand>();
}
