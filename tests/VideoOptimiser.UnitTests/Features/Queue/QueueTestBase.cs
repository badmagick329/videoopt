using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests.Features.Queue;

/// <summary>
/// What the tests of the queue commands share: a temporary workspace holding the database, the watch folder and the Immich
/// staging folder; jobs set up straight in the repository (one step at a time, as a run or a crash would leave them); and the
/// real CLI run against a config written beside the database. Each test class says which fakes it registers
/// (<see cref="RegisterFakes"/>) and what its config adds to the database path (<see cref="DefaultYaml"/>).
/// </summary>
public abstract class QueueTestBase : IDisposable
{
    protected QueueTestBase() => Directory.CreateDirectory(Root);

    protected string Workspace { get; } = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    protected SqliteJobRepository Repository { get; } = new(new SqliteDatabaseInitializer());

    /// <summary>The config file; empty until the first run writes <see cref="DefaultYaml"/> or a test writes its own.</summary>
    protected string Config { get; private set; } = string.Empty;

    protected string DatabasePath => Path.Combine(Workspace, "jobs.db");
    protected string Root => Path.Combine(Workspace, "Videos");
    protected string StagingDirectory => Path.Combine(Workspace, "staging");
    protected string ConfigArgument => $" --config \"{Config}\"";

    /// <summary>The config lines after the database path, for a test that does not write its own.</summary>
    protected virtual string[] DefaultYaml => [];

    protected virtual void RegisterFakes(IServiceCollection services)
    {
    }

    /// <summary>Writes the config: the database, <paramref name="yaml"/>, and the Immich staging folder (with a server only when <paramref name="immichConfigured"/>).</summary>
    protected async Task WriteConfigAsync(string[]? yaml = null, bool immichConfigured = false)
    {
        string[] server = immichConfigured ? ["  baseUrl: 'http://immich.test'"] : [];
        Config = await RunCli.WriteConfigAsync(DatabasePath, [.. yaml ?? DefaultYaml, "immich:", .. server, $"  stagingDirectory: '{StagingDirectory}'"]);
    }

    protected Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) => RunAsync(_ => { }, args);

    protected async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(Action<IServiceCollection> configure, params string[] args)
    {
        if (Config.Length == 0) await WriteConfigAsync();
        return await ConsoleCapture.RunCliAsync(services =>
        {
            RegisterFakes(services);
            configure(services);
        }, [.. args, "--config", Config]);
    }

    protected static async Task<string> FileAsync(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        await File.WriteAllTextAsync(path, "original video bytes");
        return path;
    }

    /// <summary>A queued job for a new file in the watch folder; run order is the order of the calls.</summary>
    protected async Task<JobRecord> QueuedAsync(string name, Action<JobRecord>? configure = null)
    {
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = await FileAsync(Root, name), SourceFingerprint = "original", Status = JobStatus.Queued };
        configure?.Invoke(job);
        await Repository.CreateAsync(DatabasePath, job);
        RunCli.AfterCreate(job);
        return job;
    }

    protected async Task MoveAsync(JobRecord job, params JobStatus[] steps)
    {
        foreach (var step in steps)
        {
            job.Status = step;
            await Repository.UpdateAsync(DatabasePath, job);
        }
    }

    /// <summary>Runs the job to <paramref name="stage"/> and stops it there, as a run's Ctrl+C does.</summary>
    protected async Task StopAsync(JobRecord job, JobStatus stage)
    {
        await MoveAsync(job, stage);
        job.ResumeStatus = stage;
        await MoveAsync(job, JobStatus.Interrupted);
    }

    protected async Task<JobRecord> HeldAsync(JobRecord job)
    {
        (await Repository.SetHeldAsync(DatabasePath, job, held: true)).Should().BeTrue();
        return job;
    }

    protected async Task<JobRecord> StoredAsync(JobRecord job) => (await Repository.GetAsync(DatabasePath, job.Id))!;

    public void Dispose()
    {
        Directory.Delete(Workspace, recursive: true);
        GC.SuppressFinalize(this);
    }
}
