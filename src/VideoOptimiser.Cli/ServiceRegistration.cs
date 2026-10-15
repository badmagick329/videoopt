using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Calibration;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Cli.Features.Config;
using VideoOptimiser.Cli.Features.Doctor;
using VideoOptimiser.Infrastructure.Calibration;
using VideoOptimiser.Infrastructure.Configuration;
using VideoOptimiser.Infrastructure.Eligibility;
using VideoOptimiser.Infrastructure.Immich;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.Processing;
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
        .AddImmichRun()
        .AddValidate()
        .AddFinalize()
        .AddCleanup()
        .AddCalibrate();

    public static IServiceCollection AddSharedKernel(this IServiceCollection services)
    {
        services.AddSingleton<IConfigurationLoader, YamlConfigurationLoader>();
        services.AddSingleton<ISettingsValidator, SettingsValidator>();
        services.AddSingleton<IDatabaseInitializer, SqliteDatabaseInitializer>();
        services.AddSingleton<IDatabaseLocks, DatabaseLocks>();
        services.AddSingleton<IJobRepository, SqliteJobRepository>();
        services.AddSingleton<IImmichClientFactory, ImmichClientFactory>();
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
        return services;
    }

    public static IServiceCollection AddWorker(this IServiceCollection services) =>
        services.AddSingleton<IRemoteWorkerLifecycle, HetznerRemoteWorkerLifecycle>();

    public static IServiceCollection AddRemoteSetup(this IServiceCollection services) =>
        services.AddSingleton<IRemoteSetupService, RemoteSetupService>();

    // IQueueService also serves the run until VOPT-99 and VOPT-101 split it.
    public static IServiceCollection AddQueue(this IServiceCollection services) => services
        .AddSingleton<IQueueService, QueueService>()
        .AddSingleton<IJobRetryService, JobRetryService>();

    public static IServiceCollection AddRun(this IServiceCollection services) => services
        .AddSingleton<IProcessingSessionFactory, ProcessingSessionFactory>()
        .AddSingleton<IJobProcessor, JobProcessor>()
        .AddSingleton<RunStop>()
        .AddSingleton<RunWorkerState>();

    public static IServiceCollection AddImmichRun(this IServiceCollection services) => services
        .AddSingleton<IImmichDiscoveryService>(provider => new ImmichDiscoveryService(provider.GetRequiredService<IImmichClientFactory>(), provider.GetRequiredService<IJobRepository>(), provider.GetRequiredService<Func<string, IMediaProbe>>(), provider.GetRequiredService<IFileFingerprintService>()))
        .AddSingleton<IImmichReuploadGuard>(provider => new ImmichReuploadGuard(provider.GetRequiredService<IImmichClientFactory>(), provider.GetRequiredService<IJobRepository>()))
        .AddSingleton<IImmichRunService, ImmichRunService>();

    public static IServiceCollection AddValidate(this IServiceCollection services) =>
        services.AddSingleton<IOutputValidationService, OutputValidationService>();

    public static IServiceCollection AddFinalize(this IServiceCollection services) => services
        .AddSingleton<ImmichFinalizer>()
        .AddSingleton<IFinalizationService, FinalizationService>()
        .AddSingleton<ISafeFileInstaller, SafeFileInstaller>();

    public static IServiceCollection AddCleanup(this IServiceCollection services) =>
        services.AddSingleton<ITemporaryOutputCleanupService, TemporaryOutputCleanupService>();

    // calibrate promises to write nothing, so it gets its own read-only store and a probe cache that never saves.
    public static IServiceCollection AddCalibrate(this IServiceCollection services) =>
        services.AddSingleton<ICalibrationService>(provider => CalibrationService.ReadOnly(
            provider.GetRequiredService<IFileReadinessService>(), provider.GetRequiredService<Func<string, IMediaProbe>>(), provider.GetRequiredService<IImmichClientFactory>()));
}
