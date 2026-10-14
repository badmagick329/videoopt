using FluentAssertions;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli;

namespace VideoOptimiser.UnitTests;

public sealed class UnreachableWorkerMessageTests
{
    [Theory]
    [InlineData(1, "Stopped: the worker w can't be reached, so 1 video was not converted.")]
    [InlineData(3, "Stopped: the worker w can't be reached, so 3 videos were not converted.")]
    public void TheStopLineCountsTheVideosNotConverted(int notConverted, string expected)
    {
        UnreachableWorkerMessage.Stopped("w", notConverted).Should().Be(expected);
    }

    /// <summary>Deleting the worker loses a CRF search or encode that may still be running there, and a finished encode not yet downloaded.</summary>
    [Theory]
    [InlineData(JobStatus.CrfSearching, " (the work in progress is lost)")]
    [InlineData(JobStatus.Encoding, " (the work in progress is lost)")]
    [InlineData(JobStatus.Staging, "")]
    [InlineData(JobStatus.Downloading, " (the finished encode is lost)")]
    public void AManagedWorkerSaysItBillsHowToResumeAndHowToDeleteIt(JobStatus stage, string suffix)
    {
        var command = CliCommand.Parse(["queue", "run", "--config", "my.yaml"]);

        UnreachableWorkerMessage.Lines(new JobRecord { ResumeStatus = stage }, managed: true, command, immichJobsLeft: null).Should().Equal(
            "The worker stays up, billed hourly, until it is deleted.",
            "To resume: video-optimiser queue run --config \"my.yaml\"",
            $"To delete it instead: video-optimiser worker delete --config \"my.yaml\"{suffix}");
    }

    [Fact]
    public void AManualHostHearsOnlyHowToResume()
    {
        UnreachableWorkerMessage.Lines(new JobRecord { ResumeStatus = JobStatus.Encoding }, managed: false, CliCommand.Parse(["immich", "run", "--limit", "50"]), immichJobsLeft: 7)
            .Should().Equal("To resume: video-optimiser immich run --limit 7");
    }
}
