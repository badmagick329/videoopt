using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Jobs;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// A server bills from the moment it exists, so a run creates it only when a job will reach a remote stage (VOPT-68). The real
/// CLI runs with a fake worker and a processor whose jobs either need the worker or fail a local check first.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class WorkerCreationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeWorkerLifecycle _lifecycle = new();

    public WorkerCreationTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    [Theory]
    [InlineData(new[] { "ineligible.mp4", "eligible.mp4" }, 1)]
    [InlineData(new[] { "ineligible.mp4", "ineligible-too.mp4" }, 0)]
    public async Task AQueueRunCreatesTheWorkerOnlyWhenAJobNeedsIt(string[] files, int ensured)
    {
        var config = await WriteConfigAsync();
        foreach (var file in files)
        {
            await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, file), Status = JobStatus.Queued });
            await Task.Delay(5);
        }

        var (_, output, _) = await RunAsync("queue", "run", "--config", config);

        _lifecycle.EnsureCalls.Should().Be(ensured);
        if (ensured == 0)
        {
            // Nothing was created for this run, so it says nothing about a worker and deletes none kept from an earlier run.
            output.Should().NotContain("Managed Hetzner worker");
            _lifecycle.DeleteCalls.Should().Be(0);
        }
    }

    [Fact]
    public async Task ProcessCreatesNoWorkerForAFileThatFailsALocalCheck()
    {
        var config = await WriteConfigAsync();
        var source = Path.Combine(_directory, "ineligible.mp4");
        await File.WriteAllBytesAsync(source, new byte[16]);

        var (code, output, _) = await RunAsync("process", source, "--config", config);

        _lifecycle.EnsureCalls.Should().Be(0);
        output.Should().NotContain("Managed Hetzner worker");
        code.Should().Be((int)ExitCode.NoEligibleFiles);
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) =>
        ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            CliFakes.NeverConfirm(services);
            services.AddSingleton<IRemoteWorkerLifecycle>(_lifecycle);
            services.AddSingleton<IJobProcessor, IneligibleByNameProcessor>();
        }, args);

    private async Task<string> WriteConfigAsync()
    {
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, string.Join('\n',
            "database:",
            $"  path: '{DatabasePath}'",
            "processing:",
            "  mode: \"remoteSsh\"",
            "  remoteSsh:",
            "    lifecycle: \"hetzner\"",
            ""));
        return config;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>A file named "ineligible…" fails the eligibility check locally, as <see cref="JobProcessor"/> would; any other is made ready.</summary>
    private sealed class IneligibleByNameProcessor : IJobProcessor
    {
        private static bool Ineligible(string path) => Path.GetFileName(path).StartsWith("ineligible", StringComparison.Ordinal);

        public Task<bool> NeedsWorkerAsync(string databasePath, string sourcePath, AppSettings settings, bool force, CancellationToken cancellationToken = default) =>
            Task.FromResult(!Ineligible(sourcePath));

        public async Task<JobProcessingResult> ProcessAsync(string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
            var job = await repository.FindOpenBySourceAsync(databasePath, Path.GetFullPath(sourcePath), cancellationToken)
                ?? await repository.CreateAsync(databasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.GetFullPath(sourcePath), Status = JobStatus.Queued }, cancellationToken);
            if (Ineligible(sourcePath))
            {
                job.Status = JobStatus.Failed;
                job.FailureCategory = "Ineligible";
                job.FailureMessage = "Codec av1 is not in any rule.";
                job.CompletedUtc = DateTimeOffset.UtcNow;
                await repository.UpdateAsync(databasePath, job, cancellationToken);
                return new JobProcessingResult(job, ExitCode.NoEligibleFiles, job.FailureMessage);
            }
            foreach (var step in new[] { JobStatus.Validating, JobStatus.ReadyToFinalize }) { job.Status = step; await repository.UpdateAsync(databasePath, job, cancellationToken); }
            return new JobProcessingResult(job, ExitCode.Success, "ready");
        }
    }
}
