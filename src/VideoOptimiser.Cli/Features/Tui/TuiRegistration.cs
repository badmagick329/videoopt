using Microsoft.Extensions.DependencyInjection;

namespace VideoOptimiser.Cli.Features.Tui;

internal static class TuiRegistration
{
    public static IServiceCollection AddTui(this IServiceCollection services) => services
        .AddSingleton<TuiSnapshotReader>()
        .AddSingleton<IFileOpener, ShellFileOpener>()
        .AddSingleton<TuiActions>()
        .AddSingleton<ITuiScreen, TuiScreen>()
        .AddSingleton<TuiCommand>();
}
