using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Doctor;
using VideoOptimiser.Infrastructure.Features.Doctor;

namespace VideoOptimiser.Cli.Features.Doctor;

internal static class DoctorRegistration
{
    public static IServiceCollection AddDoctor(this IServiceCollection services) => services
        .AddSingleton<IToolVerifier, ProcessToolVerifier>()
        .AddSingleton<IRemoteEnvironmentVerifier, ProcessRemoteEnvironmentVerifier>()
        .AddSingleton<IDoctorService, DoctorService>()
        .AddSingleton<DoctorCommand>();
}
