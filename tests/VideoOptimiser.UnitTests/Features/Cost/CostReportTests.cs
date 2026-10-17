using FluentAssertions;
using VideoOptimiser.Application.Features.Cost;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.UnitTests.Features.Cost;

/// <summary>
/// One case per branch of epic VOPT-126's *The figures*, over records built in memory. Times are minutes after
/// <see cref="Start"/>; the worker bills EUR 0.12 an hour, so a minute costs EUR 0.002. The SQL read and the wording are
/// <see cref="CostCommandTests"/>'.
/// </summary>
public sealed class CostReportTests
{
    private const decimal Price = 0.12m;
    private const long Saved = 500_000_000_000;
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10, 1)]
    [InlineData(60, 1)]
    [InlineData(61, 2)]
    public void AWorkerBillsEveryStartedHour(int minutesUp, int hoursBilled)
    {
        var report = Build(new([Worker(deleted: minutesUp)], [], [], []), now: 600);

        report.Lifetime.BilledEur.Should().Be(hoursBilled * Price);
        report.Lifetime.StillBilling.Should().BeFalse();
    }

    [Fact]
    public void AWorkerStillUpBillsToNow()
    {
        var report = Build(new([Worker(deleted: null)], [], [], []), now: 150);

        report.Lifetime.Should().Be(new CostLifetime(3 * Price, 0, 0, null, 1, StillBilling: true, EndEstimated: false, PriceMissing: false));
    }

    /// <summary>The video pays for its own ten minutes, though the worker bills a whole hour.</summary>
    [Fact]
    public void AVideoPaysForItsOwnTimeUnrounded()
    {
        var run = Guid.NewGuid();
        var job = Job(JobStatus.Completed);

        var report = Build(new([Worker(deleted: 30)], [Run(run, 0, 30)], [Span(job, run, 5, 15)], [job]));

        var video = report.Runs.Single().Videos.Single();
        video.Should().Be(new CostVideo(job.Id, "holiday.mp4", TimeSpan.FromMinutes(10), 0.02m, Saved, CostVideoState.Freed, JobStatus.Completed, 0.04m, PriceMissing: false));
        report.Lifetime.EurPerTerabyte.Should().Be(0.24m);
    }

    /// <summary>
    /// The run pays from the worker's creation to the run's end, bootstrap and idle time included; the local run after it
    /// is not listed and pays nothing, though the worker idles through it.
    /// </summary>
    [Fact]
    public void ARunPaysForTheTimeItOverlappedItsWorker()
    {
        var managed = Guid.NewGuid();
        var job = Job(JobStatus.Completed);
        var records = new CostRecords([Worker(created: 10, deleted: 120)], [Run(managed, 0, 70), Run(Guid.NewGuid(), 80, 100, worker: null)], [Span(job, managed, 20, 30)], [job]);

        var report = Build(records);

        var run = report.Runs.Should().ContainSingle().Subject;
        (run.Id, run.WorkerTime, run.Eur, run.FreedBytes, run.EurPerTerabyte).Should().Be((managed, TimeSpan.FromMinutes(60), Price, Saved, 0.24m));
    }

    [Fact]
    public void AVideoResumedInALaterRunBelongsToThatRunWithAllItsTime()
    {
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        var job = Job(JobStatus.ReadyToFinalize);

        var report = Build(new([Worker(deleted: null)], [Run(first, 0, 30), Run(second, 40, 60)], [Span(job, first, 5, 25), Span(job, second, 45, 55)], [job]), now: 70);

        report.Runs.Select(run => (run.Id, run.Videos.Count, run.WaitingBytes)).Should().Equal((second, 1, Saved), (first, 0, 0L));
        report.Runs[0].Videos.Single().WorkerTime.Should().Be(TimeSpan.FromMinutes(30));
    }

    /// <summary>Only a finalised video has freed anything; a ready one waits for review; any other ends with its time and no bytes, so every EUR per TB is a dash.</summary>
    [Theory]
    [InlineData(JobStatus.Completed, CostVideoState.Freed, Saved, 0L)]
    [InlineData(JobStatus.ReadyToFinalize, CostVideoState.WaitingForReview, 0L, Saved)]
    [InlineData(JobStatus.Failed, CostVideoState.NothingSaved, 0L, 0L)]
    public void AVideoCountsAsFreedOnlyOnceFinalised(JobStatus status, CostVideoState state, long freed, long waiting)
    {
        var run = Guid.NewGuid();
        var job = Job(status);

        var report = Build(new([Worker(deleted: 60)], [Run(run, 0, 60)], [Span(job, run, 0, 30)], [job]));

        var cost = report.Runs.Single();
        cost.Videos.Single().State.Should().Be(state);
        (cost.FreedBytes, cost.WaitingBytes, report.Lifetime.FreedBytes, report.Lifetime.WaitingBytes).Should().Be((freed, waiting, freed, waiting));
        bool[] dashes = [cost.Videos.Single().EurPerTerabyte is null, cost.EurPerTerabyte is null, report.Lifetime.EurPerTerabyte is null];
        dashes.Should().AllBeEquivalentTo(freed == 0);
    }

    [Fact]
    public void ARunThatNeverRecordedItsEndEndsWithItsLastSpan()
    {
        var run = Guid.NewGuid();
        var job = Job(JobStatus.Completed);

        var report = Build(new([Worker(deleted: null)], [Run(run, 0, null)], [Span(job, run, 5, 25)], [job]), now: 300);

        report.Runs.Single().EndUtc.Should().Be(At(25));
    }

    /// <summary>A killed process left the span open; the next run needed the run lock, so the span ended before that run began.</summary>
    [Fact]
    public void ASpanAKilledRunLeftOpenEndsWhenTheNextRunStarts()
    {
        var (killed, next) = (Guid.NewGuid(), Guid.NewGuid());
        var job = Job(JobStatus.Failed);

        var report = Build(new([Worker(deleted: null)], [Run(killed, 0, null), Run(next, 40, 50, worker: null)], [Span(job, killed, 10, null)], [job]), now: 300);

        report.Runs.Single().Videos.Single().WorkerTime.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void AWorkerDeletedOutsideTheAppIsFlaggedAsAnEstimatedEnd()
    {
        var report = Build(new([Worker(deleted: 30) with { EndEstimated = true }], [], [], []));

        report.Lifetime.EndEstimated.Should().BeTrue();
    }

    /// <summary>A worker with no price keeps its time but no EUR, and leaves its bill and its videos out of the lifetime figure.</summary>
    [Fact]
    public void AWorkerWithNoPriceShowsItsTimeAndIsLeftOutOfTheLifetime()
    {
        var (priced, unpriced) = (Guid.NewGuid(), Guid.NewGuid());
        var (kept, left) = (Job(JobStatus.Completed), Job(JobStatus.Completed));
        var records = new CostRecords(
            [Worker(deleted: 60), Worker(id: 2, price: null, created: 100, deleted: 160)],
            [Run(priced, 0, 60), Run(unpriced, 100, 160, worker: 2)],
            [Span(kept, priced, 0, 30), Span(left, unpriced, 100, 130, worker: 2)],
            [kept, left]);

        var report = Build(records);

        var run = report.Runs[0];
        (run.Id, run.WorkerTime, run.Eur, run.EurPerTerabyte, run.PriceMissing, run.Videos.Single().Eur).Should().Be((unpriced, TimeSpan.FromHours(1), (decimal?)null, (decimal?)null, true, (decimal?)null));
        report.Lifetime.Should().Be(new CostLifetime(Price, Saved, 0, 0.24m, 2, StillBilling: false, EndEstimated: false, PriceMissing: true));
    }

    private static CostReport Build(CostRecords records, int now = 1000) => CostReportService.Build(records, At(now));

    private static DateTimeOffset At(int minutes) => Start.AddMinutes(minutes);

    private static CostWorkerRow Worker(long id = 1, decimal? price = Price, int created = 0, int? deleted = 60) =>
        new(id, $"video-optimiser-{id}", price, At(created), deleted is { } end ? At(end) : null, EndEstimated: false);

    private static CostRunRow Run(Guid id, int started, int? ended, long? worker = 1) => new(id, At(started), ended is { } end ? At(end) : null, worker);

    private static CostSpanRow Span(CostJobRow job, Guid run, int started, int? ended, long worker = 1) => new(job.Id, run, worker, At(started), ended is { } end ? At(end) : null);

    private static CostJobRow Job(JobStatus status) => new(Guid.NewGuid(), @"C:\Videos\holiday.mp4", status, Saved + 100_000_000_000, 100_000_000_000);
}
