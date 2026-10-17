using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Cost;
using VideoOptimiser.Infrastructure.Features.Cost;

namespace VideoOptimiser.Cli.Features.Cost;

internal static class CostRegistration
{
    public static IServiceCollection AddCost(this IServiceCollection services) => services
        .AddSingleton<ICostRecords, SqliteCostRecords>()
        .AddSingleton<ICostReport, CostReportService>()
        .AddSingleton<CostCommand>();
}
