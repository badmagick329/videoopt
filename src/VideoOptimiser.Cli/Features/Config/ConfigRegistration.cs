using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Config;
using VideoOptimiser.Infrastructure.Features.Config;

namespace VideoOptimiser.Cli.Features.Config;

internal static class ConfigRegistration
{
    public static IServiceCollection AddConfig(this IServiceCollection services) => services
        .AddSingleton<IConfigurationTemplateWriter, YamlConfigurationTemplateWriter>()
        .AddSingleton<ConfigCommands>();
}
