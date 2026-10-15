using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Validate;

namespace VideoOptimiser.Cli.Features.Validate;

internal static class ValidateRegistration
{
    public static IServiceCollection AddValidate(this IServiceCollection services) => services
        .AddSingleton<IOutputValidationService, OutputValidationService>()
        .AddSingleton<ValidateCommand>();
}
