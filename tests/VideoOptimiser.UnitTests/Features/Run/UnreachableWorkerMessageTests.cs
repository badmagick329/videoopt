using FluentAssertions;
using VideoOptimiser.Cli.Features.Run;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.UnitTests.Features.Run;

public sealed class UnreachableWorkerMessageTests
{
    [Fact]
    public void TheStopLineCountsTheVideosNotConverted()
    {
        UnreachableWorkerMessage.Stopped("w", 1).Should().Be("Stopped: the worker w can't be reached, so 1 video was not converted.");
    }

    /// <summary>Deleting the worker loses a CRF search or encode that may still be running there, and a finished encode not yet downloaded.</summary>
    [Theory]
    [InlineData(JobStatus.Staging, "")]
    [InlineData(JobStatus.Downloading, " (the finished encode is lost)")]
    public void AManagedWorkerSaysItBillsHowToResumeAndHowToDeleteIt(JobStatus stage, string suffix)
    {
        var command = CliCommand.Parse(["run", "--config", "my.yaml"]);

        UnreachableWorkerMessage.Lines(new JobRecord { ResumeStatus = stage }, managed: true, command).Should().Equal(
            "The worker stays up, billed hourly, until it is deleted.",
            "To resume: video-optimiser run --config \"my.yaml\"",
            $"To delete it instead: video-optimiser worker delete --config \"my.yaml\"{suffix}");
    }
}
