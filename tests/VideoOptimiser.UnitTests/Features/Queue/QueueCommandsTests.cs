using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests.Features.Queue;

/// <summary>
/// The branches of the queue commands that no other CLI test pins, with whole output and exit code, so the move of their rules
/// out of the CLI (VOPT-99) is checked against today's behaviour: the job lists (<c>queue list</c>, <c>status</c>,
/// <c>history</c>, with <c>--json</c> byte for byte), <c>queue drop --all</c> beside a live run or finalise, a lost race and a
/// remote stage, a dropped job's staged copy, <c>queue retry</c> refusals, and two <c>queue add &lt;file&gt;</c> lines.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class QueueCommandsTests : IDisposable
{
    private static readonly DateTimeOffset Moment = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private string _config = string.Empty;

    public QueueCommandsTests()
    {
        Directory.CreateDirectory(Root);
        _config = Path.Combine(_directory, "config.yaml");
        File.WriteAllText(_config, string.Join('\n',
            "database:",
            $"  path: '{DatabasePath}'",
            "watch:",
            "  roots:",
            $"    - path: '{Root}'",
            "eligibility:",
            "  rules:",
            "    - codecs: ['h264']",
            "      resolution: '1080p-1440p'",
            "      minimumVideoBitrate: '1Mbps'",
            "      minimumFileSize: '1B'",
            "savings:",
            "  minimumBytesSaved: '1B'",
            "immich:",
            "  baseUrl: 'http://immich.test'",
            $"  stagingDirectory: '{StagingDirectory}'",
            ""));
    }

    private string DatabasePath => Path.Combine(_directory, "jobs.db");
    private string Root => Path.Combine(_directory, "Videos");
    private string StagingDirectory => Path.Combine(_directory, "staging");
    private string ConfigArgument => $" --config \"{_config}\"";

    [Theory]
    [InlineData("No active jobs.", "queue", "list")]
    [InlineData("No active jobs.", "status")]
    [InlineData("No job history.", "history")]
    public async Task AnEmptyListSaysSo(string line, params string[] args)
    {
        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new ListingRepository(_repository, [], [])), args);

        result.Should().Be((0, Lines(line), ""));
    }

    [Theory]
    [InlineData("queue", "list")]
    [InlineData("status")]
    public async Task TheActiveListHasOneLinePerJobWithWhatIsKnownOfIt(params string[] args)
    {
        var waiting = Job("clip-a.mp4", JobStatus.Queued);
        var stopped = Job("clip-b.mp4", JobStatus.Interrupted, job => { job.ResumeStatus = JobStatus.Validating; job.Crf = 27; });
        var immich = Job("IMG_0001.MOV", JobStatus.ReadyToFinalize, job =>
        {
            job.SourceKind = JobSourceKind.Immich;
            job.ImmichAssetId = "asset-1";
            job.ImmichNewAssetId = "asset-2";
            job.Crf = 30;
            job.PercentageSaved = 41.5m;
        });

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new ListingRepository(_repository, [waiting, stopped, immich], [])), args);

        result.Should().Be((0, Lines(
            $"{waiting.Id:N}  Queued           clip-a.mp4",
            $"{stopped.Id:N}  Interrupted      clip-b.mp4 CRF 27 (resume revalidates the existing output)",
            $"{immich.Id:N}  ReadyToFinalize  IMG_0001.MOV Immich asset-1 -> asset-2 CRF 30 41.5% saved"), ""));
    }

    [Fact]
    public async Task HistoryListsTheEndedJobsWithTheirFailure()
    {
        var failed = Job("clip-a.mp4", JobStatus.Failed, job => { job.FailureCategory = "EncodingFailed"; job.FailureMessage = "ab-av1 exited with 1."; });
        var completed = Job("clip-b.mp4", JobStatus.Completed, job => { job.Crf = 28; job.PercentageSaved = 60.0m; });

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new ListingRepository(_repository, [Job("open.mp4", JobStatus.Queued)], [failed, completed])), "history");

        result.Should().Be((0, Lines(
            $"{failed.Id:N}  Failed           clip-a.mp4 — EncodingFailed: ab-av1 exited with 1.",
            $"{completed.Id:N}  Completed        clip-b.mp4 CRF 28 60.0% saved"), ""));
    }

    [Theory]
    [InlineData("queue", "list", "--json")]
    [InlineData("status", "--json")]
    public async Task TheJsonListIsCamelCaseIndentedWithNullsKept(params string[] args)
    {
        var file = Job("clip-a.mp4", JobStatus.Queued, job => job.Crf = 27);
        var immich = Job("IMG_0001.MOV", JobStatus.Failed, job =>
        {
            job.SourceKind = JobSourceKind.Immich;
            job.ImmichAssetId = "asset-1";
            job.ImmichNewAssetId = "asset-2";
            job.OutputPath = "out/IMG_0001.mp4";
            job.PercentageSaved = 12.5m;
            job.FailureCategory = "EncodingFailed";
            job.FailureMessage = "ab-av1 exited with 1.";
        });

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new ListingRepository(_repository, [file, immich], [])), args);

        var expected = $$"""
            [
              {
                "id": "{{file.Id:N}}",
                "status": "Queued",
                "sourcePath": {{Json(file.SourcePath)}},
                "source": "file",
                "immichAssetId": null,
                "immichNewAssetId": null,
                "crf": 27,
                "outputPath": null,
                "percentageSaved": null,
                "failureCategory": null,
                "failureMessage": null,
                "updatedUtc": "2026-03-04T05:06:07+00:00"
              },
              {
                "id": "{{immich.Id:N}}",
                "status": "Failed",
                "sourcePath": {{Json(immich.SourcePath)}},
                "source": "immich",
                "immichAssetId": "asset-1",
                "immichNewAssetId": "asset-2",
                "crf": null,
                "outputPath": "out/IMG_0001.mp4",
                "percentageSaved": 12.5,
                "failureCategory": "EncodingFailed",
                "failureMessage": "ab-av1 exited with 1.",
                "updatedUtc": "2026-03-04T05:06:07+00:00"
              }
            ]
            """;
        result.Should().Be((0, expected.ReplaceLineEndings() + Environment.NewLine, ""));
    }

    [Fact]
    public async Task TheJsonHistoryOfNothingIsAnEmptyArray()
    {
        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new ListingRepository(_repository, [], [])), "history", "--json");

        result.Should().Be((0, Lines("[]"), ""));
    }

    /// <summary>A video converting under a live run is not the queue's to drop; the run keeps it, and the rest of the queue still goes.</summary>
    [Fact]
    public async Task DropAllLeavesAVideoConvertingUnderALiveRun()
    {
        var waiting = await QueuedAsync("clip-a.mp4");
        var converting = await QueuedAsync("clip-b.mp4");
        await MoveAsync(converting, JobStatus.Encoding);
        using var run = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), "video-optimiser run", out _);

        var result = await RunAsync("queue", "drop", "--all");

        result.Should().Be((0, Lines("Dropped clip-a.mp4."), ""));
        (await StoredAsync(waiting)).Status.Should().Be(JobStatus.Cancelled);
        (await StoredAsync(converting)).Status.Should().Be(JobStatus.Encoding);
    }

    [Fact]
    public async Task DropAllLeavesAVideoBeingFinalisedByALiveFinaliseWithoutAWord()
    {
        var waiting = await QueuedAsync("clip-a.mp4");
        var finalising = await QueuedAsync("clip-b.mp4");
        await MoveAsync(finalising, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);
        using var finalise = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(DatabasePath), "finalize", out _);

        var result = await RunAsync("queue", "drop", "--all");

        result.Should().Be((0, Lines("Dropped clip-a.mp4."), ""));
        (await StoredAsync(waiting)).Status.Should().Be(JobStatus.Cancelled);
        (await StoredAsync(finalising)).Status.Should().Be(JobStatus.Finalizing);
    }

    [Fact]
    public async Task DropAllSaysWhenAStoppedRemoteStageMayStillBeRunning()
    {
        var job = await QueuedAsync("clip-a.mp4", queued => { queued.ExecutionMode = ProcessingModes.RemoteSsh; queued.RemoteHost = "video-worker"; queued.RemoteWorkspace = $"/w/{queued.Id:N}"; });
        await StopAsync(job, JobStatus.CrfSearching);

        var result = await RunAsync("queue", "drop", "--all");

        result.Should().Be((0, Lines("Dropped clip-a.mp4.", "Its CRF search may still be running on the worker until it finishes."), ""));
    }

    /// <summary>A video another terminal changed since the list was read is reported on stderr and exits 1; the rest still go.</summary>
    [Fact]
    public async Task DropAllReportsAVideoThatChangedUnderItAndDropsTheRest()
    {
        var first = await QueuedAsync("clip-a.mp4");
        var second = await QueuedAsync("clip-b.mp4");
        var racing = new RacingDropRepository(_repository, first.Id);

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(racing), "queue", "drop", "--all");

        result.Should().Be((1, Lines("Dropped clip-b.mp4."), Lines($"Job {first.Id:N} changed while this command was using it. Run the command again.")));
        (await StoredAsync(first)).Status.Should().Be(JobStatus.Queued);
        (await StoredAsync(second)).Status.Should().Be(JobStatus.Cancelled);
    }

    /// <summary>A failed Immich job's staged copy is cleanup's to delete, so the refusal points at cleanup though no AV1 is left.</summary>
    [Fact]
    public async Task DroppingAnEndedImmichVideoWithAStagedCopyPointsAtCleanup()
    {
        var assetId = Guid.NewGuid().ToString("D");
        var source = Path.Combine(StagingDirectory, assetId, "IMG_0001.MOV");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "original");
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = "original", Status = JobStatus.Queued, SourceKind = JobSourceKind.Immich, ImmichAssetId = assetId, ImmichChecksum = "checksum" };
        await _repository.CreateAsync(DatabasePath, job);
        await MoveAsync(job, JobStatus.Failed);

        var result = await RunAsync("queue", "drop", job.Id.ToString("N"));

        result.Should().Be((1, "", Lines("IMG_0001.MOV has already failed, so there is nothing to drop.", $"'video-optimiser cleanup{ConfigArgument}' deletes its temporary files.")));
    }

    [Fact]
    public async Task RetryRefusesAJobIdThatIsNotOne()
    {
        (await RunAsync("queue", "retry", "3f2a")).Should().Be((2, "", Lines("Job ID is invalid.")));
    }

    [Theory]
    [InlineData("unknown", "Job was not found.")]
    [InlineData("queued", "Only failed, cancelled or skipped jobs can be retried.")]
    public async Task RetryRefusesAJobItCannotRequeue(string which, string refusal)
    {
        var id = which == "queued" ? (await QueuedAsync("clip-a.mp4")).Id : Guid.NewGuid();

        (await RunAsync("queue", "retry", id.ToString("N"))).Should().Be((2, "", Lines(refusal)));
    }

    [Fact]
    public async Task RetryOfAnImmichVideoWhoseCopyIsStillThereAddsNoDownloadLine()
    {
        var source = Path.Combine(StagingDirectory, "asset", "IMG_0001.MOV");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "original");
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = "original", Status = JobStatus.Queued, SourceKind = JobSourceKind.Immich, ImmichAssetId = "asset", ImmichChecksum = "checksum" };
        await _repository.CreateAsync(DatabasePath, job);
        await MoveAsync(job, JobStatus.Failed);

        var (code, output, error) = await RunAsync("queue", "retry", job.Id.ToString("N"));

        var replacement = (await _repository.FindOpenByImmichAssetAsync(DatabasePath, "asset"))!;
        (code, output, error).Should().Be((0, Lines($"Queued IMG_0001.MOV again as job {replacement.Id:N}.", $"Next: run 'video-optimiser run{ConfigArgument}'."), ""));
    }

    [Fact]
    public async Task AddingAFileWithOneVideoAheadSaysSoInTheSingular()
    {
        await QueuedAsync("clip-a.mp4");
        var path = Path.Combine(Root, "clip-b.mp4");
        await File.WriteAllTextAsync(path, "original video bytes");

        var (code, output, error) = await RunAsync(services =>
        {
            services.AddSingleton<IFileReadinessService>(new ReadyReadiness());
            services.AddSingleton<Func<string, IMediaProbe>>(_ => new H264Probe());
        }, "queue", "add", path);

        var job = (await _repository.FindOpenBySourceAsync(DatabasePath, path))!;
        (code, output, error).Should().Be((0, Lines($"Queued clip-b.mp4 as job {job.Id:N}.", $"1 video is ahead of it; 'video-optimiser queue next {job.Id:N}{ConfigArgument}' puts it first."), ""));
    }

    /// <summary>A finalisation a crash left reads Finalizing until a run marks it; with no finalise going it is stopped, not finalising.</summary>
    [Fact]
    public async Task AddingAFileWhoseFinalisationACrashLeftNamesItStopped()
    {
        var job = await QueuedAsync("clip-a.mp4");
        await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);

        (await RunAsync("queue", "add", job.SourcePath)).Should().Be((0, Lines($"clip-a.mp4 already has job {job.Id:N} (stopped while being finalised)."), ""));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static JobRecord Job(string name, JobStatus status, Action<JobRecord>? configure = null)
    {
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(Path.GetTempPath(), "queue-list", name), SourceFingerprint = "original", Status = status, CreatedUtc = Moment, UpdatedUtc = Moment };
        configure?.Invoke(job);
        return job;
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) => RunAsync(_ => { }, args);

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(Action<IServiceCollection> configure, params string[] args) =>
        ConsoleCapture.RunCliAsync(configure, [.. args, "--config", _config]);

    private async Task<JobRecord> QueuedAsync(string name, Action<JobRecord>? configure = null)
    {
        var source = Path.Combine(Root, name);
        await File.WriteAllTextAsync(source, "original");
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = "original", Status = JobStatus.Queued };
        configure?.Invoke(job);
        await _repository.CreateAsync(DatabasePath, job);
        await Task.Delay(5);
        return job;
    }

    private async Task MoveAsync(JobRecord job, params JobStatus[] steps)
    {
        foreach (var step in steps)
        {
            job.Status = step;
            await _repository.UpdateAsync(DatabasePath, job);
        }
    }

    private async Task StopAsync(JobRecord job, JobStatus stage)
    {
        await MoveAsync(job, stage);
        job.ResumeStatus = stage;
        await MoveAsync(job, JobStatus.Interrupted);
    }

    private async Task<JobRecord> StoredAsync(JobRecord job) => (await _repository.GetAsync(DatabasePath, job.Id))!;

    /// <summary>Lists the jobs it is given, one set for the active list and one for the history, and delegates the rest.</summary>
    private sealed class ListingRepository(IJobRepository inner, IReadOnlyList<JobRecord> active, IReadOnlyList<JobRecord> ended) : DelegatingJobRepository(inner)
    {
        public override Task<IReadOnlyList<JobRecord>> ListAsync(string databasePath, bool terminal, CancellationToken cancellationToken = default) => Task.FromResult(terminal ? ended : active);
    }

    /// <summary>Changes one job from "another terminal" just before the command drops it, so the command's copy is stale.</summary>
    private sealed class RacingDropRepository(IJobRepository inner, Guid racing) : DelegatingJobRepository(inner)
    {
        public override async Task<bool> DropAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default)
        {
            if (job.Id == racing) await ChangeBehindTheCommandAsync(databasePath, job.Id, cancellationToken);
            return await Inner.DropAsync(databasePath, job, cancellationToken);
        }
    }

    private sealed class ReadyReadiness : IFileReadinessService
    {
        public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new FileReadinessResult(true, "File can be read."));
    }

    private sealed class H264Probe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaInfo("h264", 1, 0, 0, 0, null, null, 1920, 1080, 10_000_000));
    }
}
