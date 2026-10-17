using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Cli.Features.Calibrate;
using VideoOptimiser.Cli.Features.Cleanup;
using VideoOptimiser.Cli.Features.Config;
using VideoOptimiser.Cli.Features.Cost;
using VideoOptimiser.Cli.Features.Doctor;
using VideoOptimiser.Cli.Features.Finalize;
using VideoOptimiser.Cli.Features.ImmichRun;
using VideoOptimiser.Cli.Features.Queue;
using VideoOptimiser.Cli.Features.RemoteSetup;
using VideoOptimiser.Cli.Features.Run;
using VideoOptimiser.Cli.Features.Status;
using VideoOptimiser.Cli.Features.Tui;
using VideoOptimiser.Cli.Features.Validate;
using VideoOptimiser.Cli.Features.Worker;
using VideoOptimiser.Infrastructure.Configuration;
using VideoOptimiser.Infrastructure.Eligibility;
using VideoOptimiser.Infrastructure.Immich;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.Cli;

/// <summary>
/// The services each slice needs, one method per slice, so a slice's registration moves with it into its
/// <c>Features/&lt;Slice&gt;</c> folder (VOPT-93). <see cref="AddSharedKernel"/> holds what more than one slice uses.
/// </summary>
internal static class ServiceRegistration
{
    public static IServiceCollection AddVideoOptimiser(this IServiceCollection services) => services
        .AddSharedKernel()
        .AddConfig()
        .AddDoctor()
        .AddWorker()
        .AddRemoteSetup()
        .AddQueue()
        .AddRun()
        .AddStatus()
        .AddImmichRun()
        .AddValidate()
        .AddFinalize()
        .AddCleanup()
        .AddCalibrate()
        .AddCost()
        .AddTui();

    public static IServiceCollection AddSharedKernel(this IServiceCollection services)
    {
        services.AddSingleton<IConfigurationLoader, YamlConfigurationLoader>();
        services.AddSingleton<ISettingsValidator, SettingsValidator>();
        services.AddSingleton<IDatabaseInitializer, SqliteDatabaseInitializer>();
        services.AddSingleton<IDatabaseLocks, DatabaseLocks>();
        services.AddSingleton<IJobRepository, SqliteJobRepository>();
        services.AddSingleton<IImmichClientFactory, ImmichClientFactory>();
        // Shared: `queue drop` and `cleanup` both refuse to delete what may be the only copy of a video.
        services.AddSingleton<IOnlyCopyGuard, OnlyCopyGuard>();
        // Shared: `run` and `immich run` both restage a job's lost original before converting it. A lambda, so the
        // optional clock keeps its default.
        services.AddSingleton<IImmichRestaging>(provider => new ImmichRestaging(provider.GetRequiredService<IImmichClientFactory>(), provider.GetRequiredService<IJobRepository>()));
        services.AddSingleton<IFileReadinessService, FileReadinessService>();
        services.AddSingleton<Func<string, IMediaProbe>>(_ => ffprobePath => new FfprobeMediaProbe(ffprobePath));
        services.AddSingleton<Func<string, IMediaTimelineProbe>>(_ => ffprobePath => new FfprobeMediaTimelineProbe(ffprobePath));
        services.AddSingleton<IMediaProbeCache, SqliteMediaProbeCache>();
        services.AddSingleton<IFileScanner, FileScanner>();
        services.AddSingleton<Func<string, ICrfSearchClient>>(_ => abAv1Path => new AbAv1CrfSearchClient(abAv1Path));
        services.AddSingleton<Func<string, IVideoEncoder>>(_ => abAv1Path => new AbAv1VideoEncoder(abAv1Path));
        services.AddSingleton<IAbAv1ProgressTranslator, AbAv1ProgressTranslator>();
        services.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
        services.AddSingleton<Func<string, IMediaDecodeTester>>(provider => ffmpegPath => new FfmpegDecodeTester(ffmpegPath, provider.GetRequiredService<IExternalProcessRunner>()));
        services.AddSingleton<IOutputManifestStore, OutputManifestStore>();
        services.AddSingleton<IFileFingerprintService, FileFingerprintService>();
        services.AddSingleton<IConsoleInput, ConsoleInput>();
        // Injected so a test can fix "now" for what a status card says is up for how long.
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
