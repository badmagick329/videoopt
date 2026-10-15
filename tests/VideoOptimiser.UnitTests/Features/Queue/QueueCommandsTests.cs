using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using static VideoOptimiser.UnitTests.RunCli;

namespace VideoOptimiser.UnitTests.Features.Queue;

/// <summary>
/// The branches of the queue commands that no other CLI test pins, with whole output and exit code, so the move of their rules
/// out of the CLI (VOPT-99) is checked against today's behaviour: the job lists (<c>queue list</c>, <c>status</c>,
/// <c>history</c>, with <c>--json</c> byte for byte), <c>queue drop --all</c> beside a live run or finalise and after a lost
/// race, and <c>queue retry</c> refusals.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class QueueCommandsTests : QueueTestBase
{
    private static readonly DateTimeOffset Moment = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    // The empty history as JSON is here too: the array shape of an empty list is the same handler as the text.
    [Theory]
    [InlineData("No active jobs.", "queue", "list")]
    [InlineData("No job history.", "history")]
    [InlineData("[]", "history", "--json")]
    public async Task AnEmptyListSaysSo(string line, params string[] args)
    {
        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new ListingRepository(Repository, [], [])), args);

        result.Should().Be((0, Transcript(line), ""));
    }

    /// <summary>The three stopped encodes also pin what resume does with each one's temporary output, as <c>TemporaryOutputClassifier.DescribeResume</c> words it.</summary>
    [Fact]
    public async Task TheActiveListHasOneLinePerJobWithWhatIsKnownOfIt()
    {
        var waiting = Job("clip-a.mp4", JobStatus.Queued);
        var stopped = Job("clip-b.mp4", JobStatus.Interrupted, job => { job.ResumeStatus = JobStatus.Validating; job.Crf = 27; });
        var localEncode = Job("clip-c.mp4", JobStatus.Interrupted, job => { job.ResumeStatus = JobStatus.Encoding; job.Attempt = 2; });
        var remoteEncode = Job("clip-d.mp4", JobStatus.Interrupted, job => { job.ResumeStatus = JobStatus.Encoding; job.Attempt = 2; job.ExecutionMode = "remoteSsh"; });
        var finalising = Job("clip-e.mp4", JobStatus.Interrupted, job => job.ResumeStatus = JobStatus.Finalizing);
        var immich = Job("IMG_0001.MOV", JobStatus.ReadyToFinalize, job =>
        {
            job.SourceKind = JobSourceKind.Immich;
            job.ImmichAssetId = "asset-1";
            job.ImmichNewAssetId = "asset-2";
            job.Crf = 30;
            job.PercentageSaved = 41.5m;
        });

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new ListingRepository(Repository, [waiting, stopped, localEncode, remoteEncode, finalising, immich], [])), "queue", "list");

        result.Should().Be((0, Transcript(
            $"{waiting.Id:N}  Queued           clip-a.mp4",
            $"{stopped.Id:N}  Interrupted      clip-b.mp4 CRF 27 (resume revalidates the existing output)",
            $"{localEncode.Id:N}  Interrupted      clip-c.mp4 (resume starts attempt 3 and deletes the partial output of attempt 2)",
            $"{remoteEncode.Id:N}  Interrupted      clip-d.mp4 (resume reattaches to remote attempt 2)",
            $"{finalising.Id:N}  Interrupted      clip-e.mp4",
            $"{immich.Id:N}  ReadyToFinalize  IMG_0001.MOV Immich asset-1 -> asset-2 CRF 30 41.5% saved"), ""));
    }

    [Fact]
    public async Task HistoryListsTheEndedJobsWithTheirFailure()
    {
        var failed = Job("clip-a.mp4", JobStatus.Failed, job => { job.FailureCategory = "EncodingFailed"; job.FailureMessage = "ab-av1 exited with 1."; });
        var completed = Job("clip-b.mp4", JobStatus.Completed, job => { job.Crf = 28; job.PercentageSaved = 60.0m; });

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new ListingRepository(Repository, [Job("open.mp4", JobStatus.Queued)], [failed, completed])), "history");

        result.Should().Be((0, Transcript(
            $"{failed.Id:N}  Failed           clip-a.mp4 — EncodingFailed: ab-av1 exited with 1.",
            $"{completed.Id:N}  Completed        clip-b.mp4 CRF 28 60.0% saved"), ""));
    }

    /// <summary>The <c>status</c> row is the only test that <c>status</c> is routed to the active list.</summary>
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

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new ListingRepository(Repository, [file, immich], [])), args);

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

    /// <summary>
    /// A video converting under a live run, or being finalised by a live finalise, is not the queue's to drop; its owner keeps
    /// it, and the rest of the queue still goes. Neither says a word about the one it leaves.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DropAllLeavesAVideoItsRunOrFinaliseHolds(bool finalising)
    {
        var waiting = await QueuedAsync("clip-a.mp4");
        var holding = await QueuedAsync("clip-b.mp4");
        await MoveAsync(holding, finalising ? [JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing] : [JobStatus.Encoding]);
        using var live = finalising
            ? DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(DatabasePath), "finalize", out _)
            : DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), "video-optimiser run", out _);

        var result = await RunAsync("queue", "drop", "--all");

        result.Should().Be((0, Transcript("Dropped clip-a.mp4."), ""));
        (await StoredAsync(waiting)).Status.Should().Be(JobStatus.Cancelled);
        (await StoredAsync(holding)).Status.Should().Be(finalising ? JobStatus.Finalizing : JobStatus.Encoding);
    }

    /// <summary>A video another terminal changed since the list was read is reported on stderr and exits 1; the rest still go.</summary>
    [Fact]
    public async Task DropAllReportsAVideoThatChangedUnderItAndDropsTheRest()
    {
        var first = await QueuedAsync("clip-a.mp4");
        var second = await QueuedAsync("clip-b.mp4");
        var racing = new RacingDropRepository(Repository, first.Id);

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(racing), "queue", "drop", "--all");

        result.Should().Be((1, Transcript("Dropped clip-b.mp4."), Transcript($"Job {first.Id:N} changed while this command was using it. Run the command again.")));
        (await StoredAsync(first)).Status.Should().Be(JobStatus.Queued);
        (await StoredAsync(second)).Status.Should().Be(JobStatus.Cancelled);
    }

    [Theory]
    [InlineData("3f2a", "Job ID is invalid.")]
    [InlineData("unknown", "Job was not found.")]
    [InlineData("queued", "Only failed, cancelled or skipped jobs can be retried.")]
    public async Task RetryRefusesAJobItCannotRequeue(string which, string refusal)
    {
        var id = which switch { "queued" => (await QueuedAsync("clip-a.mp4")).Id.ToString("N"), "unknown" => Guid.NewGuid().ToString("N"), _ => which };

        (await RunAsync("queue", "retry", id)).Should().Be((2, "", Transcript(refusal)));
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static JobRecord Job(string name, JobStatus status, Action<JobRecord>? configure = null)
    {
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(Path.GetTempPath(), "queue-list", name), SourceFingerprint = "original", Status = status, CreatedUtc = Moment, UpdatedUtc = Moment };
        configure?.Invoke(job);
        return job;
    }

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
}
