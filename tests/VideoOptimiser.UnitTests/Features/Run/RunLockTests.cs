using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// The run lock and the finalise lock (VOPT-54 step 3). Without the run lock a second run marks the first one's active job
/// interrupted; without the finalise lock a run sets a live finalisation back, and finalize's last write then fails the job.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class RunLockTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    public RunLockTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    [Fact]
    public void ASecondHolderIsRefusedAndToldWhoHoldsTheLockUntilItIsReleased()
    {
        var path = DatabaseLock.RunPath(DatabasePath);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        using (var first = DatabaseLock.TryAcquire(path, "queue run", out _))
        {
            first.Should().NotBeNull();
            DatabaseLock.TryAcquire(path, "immich run", out var holder).Should().BeNull();
            holder.Should().NotBeNull();
            holder!.Command.Should().Be("queue run");
            holder.ProcessId.Should().Be(Environment.ProcessId);
            holder.StartedUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
        }

        using var second = DatabaseLock.TryAcquire(path, "immich run", out var none);
        second.Should().NotBeNull();
        none.Should().BeNull();
    }

    /// <summary>The holder opens the file, then writes its details; a refusal in between names nobody.</summary>
    [Fact]
    public void AHolderThatHasNotWrittenItsDetailsYetIsReportedAsStarting()
    {
        var path = DatabaseLock.RunPath(DatabasePath);
        using var starting = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);

        DatabaseLock.TryAcquire(path, "queue run", out var holder).Should().BeNull();
        holder.Should().BeNull();
    }

    [Theory]
    [InlineData("run")]
    [InlineData("immich run")]
    public async Task ASecondRunRefusesNamesTheActiveRunAndLeavesItsJobAlone(string command)
    {
        var config = await WriteConfigAsync();
        var active = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "clip.mp4"), Status = JobStatus.Encoding });
        using var running = HoldLock(DatabaseLock.RunPath(DatabasePath), "video-optimiser run", new DateTimeOffset(new DateTime(2026, 1, 2, 14, 2, 0)));
        string[] args = [.. command.Split(' '), "--config", config];

        var (code, output, error) = await ConsoleCapture.RunCliAsync(CliFakes.AcceptAnySettings, args);

        code.Should().Be((int)ExitCode.GeneralFailure);
        output.Should().BeEmpty();
        error.Should().Be(string.Join(Environment.NewLine,
            "Another run is using this database: video-optimiser run, started 2026-01-02 14:02 (process 4321).",
            "Wait for it to finish, or stop it with Ctrl+C in its window.",
            ""));
        (await _repository.GetAsync(DatabasePath, active.Id))!.Status.Should().Be(JobStatus.Encoding);
    }

    [Theory]
    [InlineData("finalize", "--ready")]
    [InlineData("immich", "finalize")]
    public async Task ASecondFinalisationRefusesAndNamesTheActiveOne(string verb, string argument)
    {
        var config = await WriteConfigAsync();
        var today = DateTime.Today;
        using var finalizing = HoldLock(DatabaseLock.FinalizePath(DatabasePath), "finalize", new DateTimeOffset(today.AddMinutes(1)));

        var (code, output, error) = await ConsoleCapture.RunCliAsync(CliFakes.AcceptAnySettings, verb, argument, "--config", config);

        code.Should().Be((int)ExitCode.GeneralFailure);
        output.Should().BeEmpty();
        // The refusal comes after a 5 s wait, which can cross midnight; the time then carries its date.
        var started = DateTime.Today == today ? "00:01" : $"{today:yyyy-MM-dd} 00:01";
        error.Should().Be($"Another finalise is running on this database, started {started} (process 4321). Wait for it to finish.{Environment.NewLine}");
    }

    /// <summary>A run holds the finalise lock only while it prepares, so finalize waits for it rather than calling it a finalise.</summary>
    [Fact]
    public async Task AFinalisationWaitsForARunToFinishPreparing()
    {
        var config = await WriteConfigAsync();
        var preparing = HoldLock(DatabaseLock.FinalizePath(DatabasePath), DatabaseLock.RunPreparing, DateTimeOffset.Now);
        _ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ => preparing.Dispose(), TaskScheduler.Default);

        var (code, output, error) = await ConsoleCapture.RunCliAsync(CliFakes.AcceptAnySettings, "finalize", "--ready", "--config", config);

        error.Should().BeEmpty();
        output.Should().Be($"No jobs are ready to finalise.{Environment.NewLine}");
        code.Should().Be((int)ExitCode.Success);
    }

    [Fact]
    public async Task ARunLeavesALiveFinalisationAlone()
    {
        var (finalizing, interruptedImmich) = await CreateFinalisationsAsync();
        using var finalizeLock = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(DatabasePath), "finalize", out _);

        var preparation = await PrepareAsync(JobSourceKind.Immich);

        (await _repository.GetAsync(DatabasePath, finalizing.Id))!.Status.Should().Be(JobStatus.Finalizing);
        var immich = (await _repository.GetAsync(DatabasePath, interruptedImmich.Id))!;
        immich.Status.Should().Be(JobStatus.Interrupted);
        immich.ResumeStatus.Should().Be(JobStatus.Finalizing);
        preparation.RunnableJobs.Should().BeEmpty();
    }

    /// <summary>With no finalisation running, a Finalizing job is one a dead process left, and the run recovers it as before.</summary>
    [Fact]
    public async Task ARunRecoversAFinalisationNoProcessHolds()
    {
        var (finalizing, interruptedImmich) = await CreateFinalisationsAsync();

        // A folder-only run, which filters by source kind, still returns an Immich finalisation to ready: the check runs first.
        var preparation = await PrepareAsync(JobSourceKind.File);

        var file = (await _repository.GetAsync(DatabasePath, finalizing.Id))!;
        file.Status.Should().Be(JobStatus.Interrupted);
        file.ResumeStatus.Should().Be(JobStatus.Finalizing);
        (await _repository.GetAsync(DatabasePath, interruptedImmich.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
        preparation.RunnableJobs.Should().BeEmpty();
        preparation.FailedJobs.Should().BeEmpty();
    }

    private async Task<(JobRecord Finalizing, JobRecord InterruptedImmich)> CreateFinalisationsAsync()
    {
        var finalizing = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "a.mp4"), Status = JobStatus.Finalizing });
        var immich = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "b.mp4"), SourceKind = JobSourceKind.Immich, ImmichAssetId = "asset-b", Status = JobStatus.Interrupted, ResumeStatus = JobStatus.Finalizing });
        return (finalizing, immich);
    }

    private Task<QueuePreparationResult> PrepareAsync(JobSourceKind source)
    {
        var settings = new AppSettings { Database = new DatabaseSettings { Path = DatabasePath } };
        return new QueueService(_repository, new NoProcessing(), new DatabaseLocks()).PrepareAsync(DatabasePath, settings, source);
    }

    /// <summary>Holds a lock as <see cref="DatabaseLock"/> does, with a start time and process ID fixed so the refusal reads the same on any clock.</summary>
    private static FileStream HoldLock(string path, string command, DateTimeOffset started)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var details = System.Text.Encoding.UTF8.GetBytes($"{command}\n{started.ToUniversalTime():O}\n4321\n");
        stream.Write(details);
        stream.Flush();
        return stream;
    }

    private Task<string> WriteConfigAsync() => RunCli.WriteConfigAsync(DatabasePath, "immich:", "  baseUrl: 'http://immich.invalid'");

    /// <summary>
    /// W5, W13: whether a run is going is read from the lock file without taking the lock, so asking never refuses a run that
    /// starts meanwhile. The file outlives its holder, so its details count only while that process lives and started
    /// before them; a reused process ID started after.
    /// </summary>
    [Fact]
    public async Task ALockFileCountsAsHeldOnlyWhileItsProcessLivesAndStartedBeforeIt()
    {
        var path = DatabaseLock.RunPath(DatabasePath);
        using var other = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var started = new DateTimeOffset(other.StartTime.ToUniversalTime());
            await File.WriteAllTextAsync(path, $"queue run\n{started.AddSeconds(1):O}\n{other.Id}\n");
            DatabaseLock.ReadLiveHolder(path)!.ProcessId.Should().Be(other.Id);

            await File.WriteAllTextAsync(path, $"queue run\n{started.AddSeconds(-1):O}\n{other.Id}\n");
            DatabaseLock.ReadLiveHolder(path).Should().BeNull("a process that started after the lock was taken reused a dead holder's ID");
        }
        finally
        {
            other.Kill(entireProcessTree: true);
            await other.WaitForExitAsync();
        }

        await File.WriteAllTextAsync(path, $"queue run\n{DateTimeOffset.UtcNow:O}\n{other.Id}\n");
        DatabaseLock.ReadLiveHolder(path).Should().BeNull("its process has ended");
    }

    /// <summary>This process's own lock counts while it holds it, though the file keeps its details after.</summary>
    [Fact]
    public void ThisProcesssOwnLockCountsOnlyWhileItIsHeld()
    {
        var path = DatabaseLock.RunPath(DatabasePath);
        using (DatabaseLock.TryAcquire(path, "queue run", out _))
        {
            DatabaseLock.ReadLiveHolder(path)!.Command.Should().Be("queue run");
        }

        DatabaseLock.ReadLiveHolder(path).Should().BeNull();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class NoProcessing : IJobProcessor
    {
        public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Preparing processes nothing.");
    }
}
