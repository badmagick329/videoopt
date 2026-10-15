using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
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
    private const string Managed = RemoteExecutionIdentity.ManagedHetzner;
    private const string Manual = "worker.example";

    // The empty history as JSON is here too: the array shape of an empty list is the same handler as the text.
    [Theory]
    [InlineData("Nothing is queued or ready to finalise.", "queue", "list")]
    [InlineData("Nothing is queued or ready to finalise.", "status")]
    [InlineData("No job history.", "history")]
    [InlineData("[]", "history", "--json")]
    public async Task AnEmptyListSaysSo(string line, params string[] args)
    {
        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new ListingRepository(Repository, [], [])), args);

        result.Should().Be((0, Transcript(line), ""));
    }

    /// <summary>
    /// Every group and entry shape in one transcript, with a run and a finalise going. The jobs are given in no particular
    /// order: the queue is numbered in run order (priority, then age), and the other groups keep that order.
    /// </summary>
    [Fact]
    public async Task TheOpenListGroupsEachJobByWhatHappensToItNext()
    {
        await WriteConfigAsync();
        var converting = Job("clip-a.mp4", JobStatus.Encoding, job => job.Crf = 30, minute: 1);
        var later = Job("clip-b.mp4", JobStatus.Queued, minute: 5);
        var first = Job("clip-c.mp4", JobStatus.Interrupted, job => { RanOn(job, Managed, JobStatus.Encoding); job.Crf = 30; }, minute: 3);
        var unreachable = Job("clip-d.mp4", JobStatus.Interrupted, job =>
        {
            RanOn(job, Managed, JobStatus.Encoding);
            (job.Crf, job.FailureCategory, job.FailureMessage) = (28, FailureCategories.RemoteUnavailable, "SSH connection failed: timed out.");
        }, minute: 4);
        var held = Job("clip-e.mp4", JobStatus.Queued, job => job.Held = true, minute: 6);
        var heldStopped = Job("IMG_0001.MOV", JobStatus.Interrupted, job => { job.SourceKind = JobSourceKind.Immich; job.Held = true; job.ResumeStatus = JobStatus.Queued; }, minute: 7);
        var ready = Job("clip-f.mp4", JobStatus.ReadyToFinalize, job => { job.Crf = 31; job.PercentageSaved = 58.3m; }, minute: 8);
        var lastFinaliseStopped = Job("IMG_0002.MOV", JobStatus.ReadyToFinalize, job =>
        {
            job.SourceKind = JobSourceKind.Immich;
            (job.Crf, job.PercentageSaved) = (29, 61.0m);
            FinalizationRules.ReturnToReady(job, FinalizationRules.InterruptedMessage);
        }, minute: 9);
        var finalising = Job("clip-h.mp4", JobStatus.Finalizing, minute: 10);
        var stoppedFinalising = Job("clip-g.mp4", JobStatus.Interrupted, job => job.ResumeStatus = JobStatus.Finalizing, minute: 11);
        // Queued last and still runs first: the run order sorts on priority before age.
        var next = Job("clip-i.mp4", JobStatus.Queued, job => job.Priority = 1, minute: 12);
        using var run = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), "video-optimiser run", out _);
        using var finalise = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(DatabasePath), "finalize", out _);
        var jobs = new[] { stoppedFinalising, held, later, finalising, unreachable, ready, heldStopped, converting, next, first, lastFinaliseStopped };

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new ListingRepository(Repository, jobs, [])), "queue", "list");

        result.Should().Be((0, Transcript(
            "Converting now",
            $"  {converting.Id:N}  Encoding  clip-a.mp4, CRF 30",
            "Queue, in run order",
            $"  1. {next.Id:N}  Waiting  clip-i.mp4",
            $"  2. {first.Id:N}  Stopped  clip-c.mp4, CRF 30: picks up the encode on the worker, or encodes again on a new one",
            $"  3. {unreachable.Id:N}  Stopped  clip-d.mp4, CRF 28: the worker couldn't be reached; picks up the encode on the worker, or encodes again on a new one",
            $"  4. {later.Id:N}  Waiting  clip-b.mp4",
            "Held",
            $"  {held.Id:N}  clip-e.mp4",
            $"  {heldStopped.Id:N}  IMG_0001.MOV (Immich), stopped",
            "Ready to finalise",
            $"  {ready.Id:N}  clip-f.mp4, CRF 31, 58.3% saved",
            $"  {lastFinaliseStopped.Id:N}  IMG_0002.MOV (Immich), CRF 29, 61.0% saved; the last finalise stopped: {FinalizationRules.InterruptedMessage}",
            $"  {finalising.Id:N}  clip-h.mp4, being finalised now",
            $"  {stoppedFinalising.Id:N}  clip-g.mp4, stopped while being finalised; 'video-optimiser finalize {stoppedFinalising.Id:N}{ConfigArgument}' checks and resumes it"), ""));
    }

    /// <summary>
    /// With no run going, a job a converting status left behind (a crash, a closed window) is a stopped one in the queue, and
    /// a finalise that is not going leaves a finalising job stopped. Neither is "Converting now" or "being finalised now".
    /// </summary>
    [Fact]
    public async Task WithNoRunGoingAConvertingJobIsListedAsStopped()
    {
        await WriteConfigAsync();
        var crashed = Job("clip-a.mp4", JobStatus.CrfSearching, minute: 1);
        var finalising = Job("clip-b.mp4", JobStatus.Finalizing, minute: 2);

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new ListingRepository(Repository, [crashed, finalising], [])), "status");

        result.Should().Be((0, Transcript(
            "Queue, in run order",
            $"  1. {crashed.Id:N}  Stopped  clip-a.mp4: searches for the CRF again",
            "Ready to finalise",
            $"  {finalising.Id:N}  clip-b.mp4, stopped while being finalised; 'video-optimiser finalize {finalising.Id:N}{ConfigArgument}' checks and resumes it"), ""));
    }

    /// <summary>One row per resume point and where the job ran; a managed worker may be gone, so it adds the new-worker fallback a manual one lacks.</summary>
    [Theory]
    [InlineData(null, JobStatus.Queued, "starts from the beginning")]
    [InlineData(null, JobStatus.Encoding, "starts the encode again")]
    [InlineData(null, JobStatus.CrfSearching, "searches for the CRF again")]
    [InlineData(null, JobStatus.Validating, "checks the AV1 again")]
    [InlineData(Managed, JobStatus.Staging, "picks up the upload, or uploads again to a new worker")]
    [InlineData(Managed, JobStatus.CrfSearching, "picks up the CRF search on the worker, or searches again on a new one")]
    [InlineData(Managed, JobStatus.Encoding, "picks up the encode on the worker, or encodes again on a new one")]
    [InlineData(Managed, JobStatus.Downloading, "downloads the AV1 from the worker, or encodes again on a new one")]
    [InlineData(Manual, JobStatus.Staging, "picks up the upload")]
    [InlineData(Manual, JobStatus.CrfSearching, "picks up the CRF search on the worker")]
    [InlineData(Manual, JobStatus.Encoding, "picks up the encode on the worker")]
    [InlineData(Manual, JobStatus.Downloading, "downloads the AV1 from the worker")]
    public async Task AStoppedJobSaysWhatTheNextRunDoesWithIt(string? worker, JobStatus? stage, string ending)
    {
        await WriteConfigAsync();
        var stopped = Job("clip-a.mp4", JobStatus.Interrupted, job =>
        {
            job.ResumeStatus = stage;
            if (worker is not null) RanOn(job, worker, stage);
        });

        var result = await RunAsync(services => services.AddSingleton<IJobRepository>(new ListingRepository(Repository, [stopped], [])), "queue", "list");

        result.Should().Be((0, Transcript("Queue, in run order", $"  1. {stopped.Id:N}  Stopped  clip-a.mp4: {ending}"), ""));
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
        var file = Job("clip-a.mp4", JobStatus.Queued, job => { job.Crf = 27; job.Held = true; });
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
                "held": true,
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
                "held": false,
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

    private static void RanOn(JobRecord job, string worker, JobStatus? stage) => (job.ExecutionMode, job.RemoteHost, job.ResumeStatus) = ("remoteSsh", worker, stage);

    /// <param name="minute">How long after the others it was queued, which sets its place in the run order.</param>
    private static JobRecord Job(string name, JobStatus status, Action<JobRecord>? configure = null, int minute = 0)
    {
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(Path.GetTempPath(), "queue-list", name), SourceFingerprint = "original", Status = status, CreatedUtc = Moment.AddMinutes(minute), UpdatedUtc = Moment };
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
