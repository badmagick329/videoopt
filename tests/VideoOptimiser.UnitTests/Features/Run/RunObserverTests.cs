using FluentAssertions;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>The observers that stand behind the event file: what the file records, how often, and how two observers share one run.</summary>
public sealed class RunObserverTests
{
    private readonly MemoryEventLog _log = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// ab-av1 reports its encode many times a second and each line is a flushed write that other processes read, so its raw
    /// lines are limited to one a second. A step or an outcome is never dropped: the file must always show where the video is.
    /// </summary>
    [Fact]
    public void RawToolLinesAreLimitedToOneASecondAndEveryOtherLineIsKept()
    {
        var sink = new EventLogRunObserver(_log, _time).ConversionStarted(2, workerUp: false);

        sink.Report(JobProgress.Tool("a"));
        _time.Advance(TimeSpan.FromMilliseconds(400));
        sink.Report(JobProgress.Tool("b"));
        sink.Report(JobProgress.Step("Encoding..."));
        sink.Report(JobProgress.Outcome(JobTone.Success, "Ready to finalise.") with { TimeLeft = TimeSpan.FromSeconds(30) });
        _time.Advance(TimeSpan.FromMilliseconds(700));
        sink.Report(JobProgress.Tool("c"));
        _time.Advance(TimeSpan.FromMilliseconds(500));
        sink.Report(JobProgress.Tool("d"));

        _log.Events.Select(entry => entry.Kind).Should().Equal(RunEventKind.ConversionStarted, RunEventKind.Progress, RunEventKind.Progress, RunEventKind.Progress, RunEventKind.Progress);
        _log.Events.Skip(1).Select(entry => entry.Text).Should().Equal("a", "Encoding...", "Ready to finalise.", "c");
        _log.Events[0].Should().Match<RunEvent>(entry => entry.Count == 2 && entry.Flag == false);
        _log.Events[3].Should().Match<RunEvent>(entry => entry.Progress == "Step" && entry.Tone == "Success" && entry.TimeLeft == TimeSpan.FromSeconds(30));
    }

    /// <summary>Enum fields are written by name, so a reader needs none of this assembly's types, and an error is its root cause's message.</summary>
    [Fact]
    public void FactsAreWrittenAsNamesAndAnErrorAsItsRootMessage()
    {
        var observer = new EventLogRunObserver(_log, _time);

        observer.WorkerKept("video-optimiser-1", RunEnd.StoppedNow, JobStatus.Encoding, answeredNo: true);
        observer.StoppedNow(null, "video-optimiser-1", WorkerFate.Keep, settingUpWorker: false);
        var exitCode = observer.RunFailed(new InvalidOperationException("outer", new IOException("disk full")));

        exitCode.Should().Be((int)ExitCode.GeneralFailure);
        _log.Events.Should().SatisfyRespectively(
            kept => (kept.Kind, kept.Worker, kept.RunEnd, kept.Stage, kept.Flag).Should().Be((RunEventKind.WorkerKept, "video-optimiser-1", "StoppedNow", "Encoding", true)),
            stopped => (stopped.Kind, stopped.Fate, stopped.Stage).Should().Be((RunEventKind.StoppedNow, "Keep", null)),
            failed => (failed.Kind, failed.Text).Should().Be((RunEventKind.RunFailed, "disk full")));
    }

    /// <summary>The event file is added beside the console: it must never change the exit code or take the sink the console reports to.</summary>
    [Fact]
    public void ATeeGivesBothObserversEveryEventAndTakesWhatItReturnsFromThePrimary()
    {
        var primary = new RecordingRunObserver { FailureExitCode = 7 };
        var secondary = new RecordingRunObserver { FailureExitCode = 8 };
        var tee = new TeeRunObserver(primary, secondary);

        tee.VideosHeld(2);
        var sink = tee.ConversionStarted(1, workerUp: true);
        sink.Report(JobProgress.Step("Encoding..."));
        tee.WorkerDeleting();
        var exitCode = tee.RunFailed(new InvalidOperationException("boom"));

        exitCode.Should().Be(7);
        string[] events = ["VideosHeld:2", "ConversionStarted:1,workerUp=True", "WorkerDeleting", "RunFailed"];
        primary.Events.Should().Equal(events);
        secondary.Events.Should().Equal(events);
        primary.ProgressTaken.Should().Equal("Encoding...");
        secondary.ProgressTaken.Should().Equal("Encoding...");
    }
}
