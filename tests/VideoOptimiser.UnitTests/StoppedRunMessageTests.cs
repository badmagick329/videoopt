using FluentAssertions;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli;

namespace VideoOptimiser.UnitTests;

public sealed class StoppedRunMessageTests
{
    [Theory]
    [InlineData(new[] { "run", "--remote", "--config", @"C:\vo\my.yaml" }, @"run --remote --config ""C:\vo\my.yaml""")]
    [InlineData(new[] { "run", "--local" }, "run --local")]
    [InlineData(new[] { "--first", "immich", "run" }, "immich run --first")]
    [InlineData(new[] { "immich", "run", "--limit", "3" }, "immich run --limit 3")]
    public void TheRerunCommandKeepsEveryFlagTheUserTyped(string[] args, string expected)
    {
        StoppedRunMessage.RerunCommand(CliCommand.Parse(args)).Should().Be(expected);
    }

    /// <summary>--limit tops the open jobs up to N, so the rerun limits to the jobs left; a typed N would download new videos on top (VOPT-69).</summary>
    [Theory]
    [InlineData(new[] { "--first", "immich", "run", "--remote" }, "immich run --limit 5 --remote")]
    [InlineData(new[] { "immich", "run", "--limit", "50" }, "immich run --limit 5")]
    [InlineData(new[] { "immich", "run" }, "immich run")]
    public void ALimitedImmichRerunLimitsItselfToTheJobsLeft(string[] args, string expected)
    {
        StoppedRunMessage.RerunCommand(CliCommand.Parse(args), immichJobsLeft: 5).Should().Be(expected);
    }

    [Fact]
    public void TheJobsLeftAreTheImmichJobsARerunWouldPickUp()
    {
        JobRecord Job(JobStatus status, JobStatus? resume = null, JobSourceKind kind = JobSourceKind.Immich, bool held = false) => new() { Status = status, ResumeStatus = resume, SourceKind = kind, Held = held };

        StoppedRunMessage.CountImmichJobsLeft(
        [
            Job(JobStatus.Queued),
            Job(JobStatus.Interrupted, JobStatus.Encoding),
            Job(JobStatus.Interrupted, JobStatus.Finalizing),
            Job(JobStatus.ReadyToFinalize),
            Job(JobStatus.Finalizing),
            Job(JobStatus.Queued, kind: JobSourceKind.File),
            // R7: a held video waits for its release, not for the rerun.
            Job(JobStatus.Queued, held: true)
        ]).Should().Be(2);
    }

    [Fact]
    public void AStoppedRemoteEncodeSaysItIsStillRunningAndBilledAndHowToContinueOrStopIt()
    {
        var command = CliCommand.Parse(["run", "--config", "my.yaml"]);

        StoppedRunMessage.Lines(Stopped(JobStatus.Encoding), "video-optimiser-1", managed: true, command).Should().Equal(
            "Stopped watching. The encode of clip.mov is still running on the worker video-optimiser-1.",
            "The worker is billed hourly until it is deleted.",
            "To pick it up again: video-optimiser run --config \"my.yaml\"",
            "To stop it instead: video-optimiser worker delete --config \"my.yaml\" (the work in progress is lost)");
    }

    /// <summary>The finished encode exists only on the worker until it is downloaded, so deleting the worker loses it.</summary>
    [Fact]
    public void AStoppedDownloadWarnsThatDeletingTheWorkerLosesTheFinishedEncode()
    {
        StoppedRunMessage.Lines(Stopped(JobStatus.Downloading), "w", managed: true, CliCommand.Parse(["run"]))[^1]
            .Should().Be("To delete it instead: video-optimiser worker delete (the finished encode is lost)");
    }

    [Fact]
    public void AStoppedUploadSaysNothingIsRunningButTheWorkerStaysUp()
    {
        StoppedRunMessage.Lines(Stopped(JobStatus.Staging), "video-optimiser-1", managed: true, CliCommand.Parse(["run"])).Should().Equal(
            "Stopped. Nothing is running on the worker video-optimiser-1, but it stays up, billed hourly, so the job can resume.",
            "To resume: video-optimiser run",
            "To delete it instead: video-optimiser worker delete");
    }

    [Theory]
    [InlineData(JobStatus.CrfSearching, true, true, "Stopped watching. The CRF search of clip.mov is still running on the worker w.")]
    [InlineData(JobStatus.Downloading, true, true, "Stopped. The encoded output is waiting on the worker w, which stays up, billed hourly.")]
    [InlineData(JobStatus.Encoding, false, true, "Stopped. Nothing is running on the worker w, but it stays up, billed hourly, so the job can resume.")]
    [InlineData(null, false, true, "Stopped. Nothing is running on the worker w, but it stays up, billed hourly, so the job can resume.")]
    [InlineData(JobStatus.Encoding, true, false, "Stopped watching. The encode of clip.mov is still running on the worker w.")]
    public void TheFirstLineNamesWhatIsLeftOnTheWorker(JobStatus? stage, bool remote, bool managed, string expected)
    {
        StoppedRunMessage.Lines(stage is null ? null : Stopped(stage.Value, remote), "w", managed, CliCommand.Parse(["run"]))[0].Should().Be(expected);
    }

    /// <summary>A manual host is neither billed nor deleted by this tool, so it hears only what is running and how to pick it up.</summary>
    [Theory]
    [InlineData(JobStatus.Encoding, 2)]
    [InlineData(JobStatus.Staging, 0)]
    public void AManualHostGetsOnlyTheLinesThatHoldForIt(JobStatus stage, int count)
    {
        StoppedRunMessage.Lines(Stopped(stage), "my-host", managed: false, CliCommand.Parse(["run"])).Should().HaveCount(count);
    }

    private static JobInterruptedException Stopped(JobStatus stage, bool remote = true) =>
        new(new JobRecord { SourcePath = @"D:\Videos\clip.mov" }, stage, remote, CancellationToken.None);
}
