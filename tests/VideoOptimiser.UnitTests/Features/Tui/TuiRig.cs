using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli;
using VideoOptimiser.Cli.Features.Tui;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests.Features.Tui;

/// <summary>
/// What the tests of the TUI's actions share: the real services behind the CLI over a temporary database, with the worker, the
/// launcher, the file opener and the finalisation swapped for fakes that record what they were asked.
/// </summary>
internal sealed class TuiRig : IDisposable
{
    public TuiRig(Action<IServiceCollection>? configure = null)
    {
        Directory.CreateDirectory(Directory_);
        Settings.Database.Path = Path.Combine(Directory_, "jobs.db");
        Settings.Processing.RemoteSsh.Lifecycle = RemoteLifecycleModes.Hetzner;
        Configuration = new LoadedConfiguration(Settings, Path.Combine(Directory_, "config.yaml"));

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHints>(new CommandHints(null));
        services.AddVideoOptimiser();
        services.AddSingleton<IRemoteWorkerLifecycle>(Worker);
        services.AddSingleton<IDetachedLauncher>(Launcher);
        services.AddSingleton<IKeepAwake>(new FakeKeepAwake());
        services.AddSingleton<IFileOpener>(Opener);
        services.AddSingleton<IFinalizationService>(Finalizer);
        configure?.Invoke(services);
        Services = services.BuildServiceProvider();
        Launcher.Scripted = true;
    }

    private string Directory_ { get; } = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public string Folder => Directory_;
    public AppSettings Settings { get; } = new();
    public LoadedConfiguration Configuration { get; }
    public ServiceProvider Services { get; }
    public FakeWorkerLifecycle Worker { get; } = new();
    public ScriptedLauncher Launcher { get; } = new();
    public RecordingOpener Opener { get; } = new();
    public RecordingFinalizer Finalizer { get; } = new();
    public SqliteJobRepository Repository { get; } = new(new SqliteDatabaseInitializer());
    public string DatabasePath => Settings.Database.Path;

    public TuiActions Actions => Services.GetRequiredService<TuiActions>();
    public TuiSnapshotReader Reader => Services.GetRequiredService<TuiSnapshotReader>();

    /// <summary>A real file in the rig's folder, so a path that must exist does.</summary>
    public string File(string name, string content = "video bytes")
    {
        var path = Path.Combine(Folder, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A queued job for a file that exists; the pause keeps run order the order of the calls.</summary>
    public async Task<JobRecord> QueuedAsync(string name, bool create = true)
    {
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = create ? File(name) : Path.Combine(Folder, name), SourceFingerprint = "original", Status = JobStatus.Queued, SourceSizeBytes = 1000 };
        await Repository.CreateAsync(DatabasePath, job);
        await Task.Delay(5);
        return job;
    }

    public async Task MoveAsync(JobRecord job, params JobStatus[] steps)
    {
        foreach (var step in steps)
        {
            job.Status = step;
            await Repository.UpdateAsync(DatabasePath, job);
        }
    }

    /// <summary>
    /// A job ready to finalise, with an AV1 on disk and the sizes its confirmation shows. <paramref name="cleanable"/> names the
    /// AV1 as the processor does (in a <c>.video-optimiser</c> folder, by job ID and attempt), so cleanup and reject find it.
    /// </summary>
    public async Task<JobRecord> ReadyAsync(string name, string extension = ".mp4", bool immich = false, bool cleanable = false)
    {
        var job = await QueuedAsync(name);
        if (cleanable)
        {
            var folder = Path.Combine(Folder, ".video-optimiser");
            System.IO.Directory.CreateDirectory(folder);
            job.Attempt = 1;
            job.OutputPath = Path.Combine(folder, $"{name}.{job.Id:N}.1.encoding{extension}");
            System.IO.File.WriteAllText(job.OutputPath, "av1");
            System.IO.File.WriteAllText(job.OutputPath + ".manifest.json", "{}");
        }
        else
        {
            job.OutputPath = File($"{name}.encoded{extension}");
        }
        job.OutputSizeBytes = 400;
        job.PercentageSaved = 60m;
        if (immich)
        {
            job.SourceKind = JobSourceKind.Immich;
            job.ImmichAssetId = "asset-" + name;
        }
        await MoveAsync(job, JobStatus.Encoding, JobStatus.Validating, JobStatus.ReadyToFinalize);
        return job;
    }

    /// <summary>A ready job whose validation warned: bulk finalise leaves it out.</summary>
    public async Task<JobRecord> WarnedAsync(string name, params string[] warnings)
    {
        var job = await ReadyAsync(name);
        job.ValidationWarnings = warnings;
        await Repository.UpdateAsync(DatabasePath, job);
        return job;
    }

    public async Task<JobRecord> StoredAsync(JobRecord job) => (await Repository.GetAsync(DatabasePath, job.Id))!;

    public void Dispose()
    {
        Services.Dispose();
        System.IO.Directory.Delete(Folder, recursive: true);
    }
}

/// <summary>Records what the TUI asked to open, and fails when told to.</summary>
internal sealed class RecordingOpener : IFileOpener
{
    public List<string> Opened { get; } = [];
    public Exception? Failure { get; set; }

    public void Open(string path)
    {
        if (Failure is not null) throw Failure;
        Opened.Add(path);
    }
}

/// <summary>Finalises nothing: it records each job it is given, in order, and answers success unless the test scripts otherwise.</summary>
internal sealed class RecordingFinalizer : IFinalizationService
{
    public List<Guid> Finalised { get; } = [];
    public Dictionary<Guid, FinalizationResult> Results { get; } = [];
    public Exception? Failure { get; set; }

    public Task<FinalizationResult> FinalizeAsync(string databasePath, Guid jobId, AppSettings settings, CancellationToken cancellationToken = default)
    {
        Finalised.Add(jobId);
        if (Failure is not null) throw Failure;
        return Task.FromResult(Results.TryGetValue(jobId, out var result) ? result : new FinalizationResult(null, ExitCode.Success, "Original deleted; the AV1 is in its place."));
    }
}
