using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

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

        var (_, output, _) = await RunAsync("run", "--config", config);

        _lifecycle.EnsureCalls.Should().Be(ensured);
        if (ensured == 0)
        {
            // Nothing was created for this run, so it says nothing about a worker and deletes none kept from an earlier run.
            output.Should().NotContain("Managed Hetzner worker");
            _lifecycle.DeleteCalls.Should().Be(0);
        }
    }

    /// <summary>
    /// A video released during a managed run that created no worker would stop at its first remote stage with nothing to
    /// connect to, so it waits for the next run, which creates one (VOPT-54 step 5).
    /// </summary>
    [Fact]
    public async Task AVideoThatJoinsARunWithNoWorkerWaitsForTheNextRun()
    {
        var config = await WriteConfigAsync();
        var local = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "ineligible.mp4"), Status = JobStatus.Queued });
        await Task.Delay(5);
        var released = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "eligible.mp4"), Status = JobStatus.Queued });
        (await _repository.SetHeldAsync(DatabasePath, released, held: true)).Should().BeTrue();
        _lifecycle.Exists = false;
        var processor = new IneligibleByNameProcessor { During = async () => (await _repository.SetHeldAsync(DatabasePath, (await _repository.GetAsync(DatabasePath, released.Id))!, held: false)).Should().BeTrue() };

        var (_, output, _) = await RunAsync(processor, "run", "--config", config);

        _lifecycle.EnsureCalls.Should().Be(0);
        output.Should().Be(string.Concat(new[]
        {
            "Encoding on a Hetzner server created for this run, billed hourly, trying cx43, cx53, cpx42, ccx33 at up to EUR 0.30/h.",
            "1 video is held; runs pass over it until it's released.",
            "Converting 1 video.",
            "",
            "[1/1] ineligible.mp4",
            "eligible.mp4 waits for the next run: it needs a worker, and this run has none.",
            "Failed: 1.",
            "",
            "Failed jobs:",
            $"{local.Id:N}  ineligible.mp4",
            "Ineligible: Codec av1 is not in any rule."
        }.Select(line => line + Environment.NewLine)));
        processor.Processed.Should().Equal("ineligible.mp4");
        (await _repository.GetAsync(DatabasePath, released.Id))!.Status.Should().Be(JobStatus.Queued);
    }

    [Fact]
    public async Task AVideoThatJoinsARunWithAWorkerIsConverted()
    {
        var config = await WriteConfigAsync();
        await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "first.mp4"), Status = JobStatus.Queued });
        await Task.Delay(5);
        var released = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "joining.mp4"), Status = JobStatus.Queued });
        (await _repository.SetHeldAsync(DatabasePath, released, held: true)).Should().BeTrue();
        var releasedOnce = false;
        var processor = new IneligibleByNameProcessor
        {
            During = async () =>
            {
                if (releasedOnce) return;
                releasedOnce = true;
                (await _repository.SetHeldAsync(DatabasePath, (await _repository.GetAsync(DatabasePath, released.Id))!, held: false)).Should().BeTrue();
            }
        };

        _ = await RunAsync(processor, "run", "--config", config);

        _lifecycle.EnsureCalls.Should().Be(1);
        processor.Processed.Should().Equal("first.mp4", "joining.mp4");
    }

    /// <summary>
    /// A video that joins a <c>--local</c> run after starting on the worker is skipped, as preparation skips one: processing
    /// it would fail it as a changed configuration and lose its CRF search or encode.
    /// </summary>
    [Fact]
    public async Task AVideoThatJoinsARunInTheOtherModeIsSkippedNotFailed()
    {
        var config = await WriteConfigAsync();
        var local = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "ineligible.mp4"), Status = JobStatus.Queued });
        await Task.Delay(5);
        var remote = new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "eligible.mp4"), Status = JobStatus.Queued, ExecutionMode = ProcessingModes.RemoteSsh, RemoteHost = RemoteExecutionIdentity.ManagedHetzner };
        remote.RemoteWorkspace = $"/var/tmp/video-optimiser/{remote.Id:N}";
        await _repository.CreateAsync(DatabasePath, remote);
        foreach (var step in new[] { JobStatus.Encoding, JobStatus.Interrupted })
        {
            remote.Status = step;
            remote.ResumeStatus = step == JobStatus.Interrupted ? JobStatus.Encoding : null;
            remote.Crf = 30;
            await _repository.UpdateAsync(DatabasePath, remote);
        }
        (await _repository.SetHeldAsync(DatabasePath, remote, held: true)).Should().BeTrue();
        _lifecycle.Exists = false;
        var processor = new IneligibleByNameProcessor { During = async () => (await _repository.SetHeldAsync(DatabasePath, (await _repository.GetAsync(DatabasePath, remote.Id))!, held: false)).Should().BeTrue() };

        var (_, output, _) = await RunAsync(processor, "run", "--local", "--config", config);

        output.Should().Be(string.Concat(new[]
        {
            "Encoding on this PC (--local).",
            "1 video is held; runs pass over it until it's released.",
            "Converting 1 video.",
            "",
            "[1/1] ineligible.mp4",
            "Skipped eligible.mp4: started on a managed Hetzner server; run without --local/--remote to resume it.",
            "Failed: 1.",
            "",
            "Failed jobs:",
            $"{local.Id:N}  ineligible.mp4",
            "Ineligible: Codec av1 is not in any rule."
        }.Select(line => line + Environment.NewLine)));
        processor.Processed.Should().Equal("ineligible.mp4");
        (await _repository.GetAsync(DatabasePath, remote.Id))!.Status.Should().Be(JobStatus.Interrupted);
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) => RunAsync(new IneligibleByNameProcessor(), args);

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(IneligibleByNameProcessor processor, params string[] args) =>
        ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            CliFakes.NotInteractive(services);
            services.AddSingleton<IRemoteWorkerLifecycle>(_lifecycle);
            services.AddSingleton<IJobProcessor>(processor);
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

        public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(!Ineligible(job.SourcePath));

        /// <summary>Runs partway through each video, as another terminal would.</summary>
        public Func<Task>? During { get; init; }
        public List<string> Processed { get; } = [];

        public async Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Processed.Add(Path.GetFileName(job.SourcePath));
            if (During is not null) await During();
            var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
            if (Ineligible(job.SourcePath))
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
