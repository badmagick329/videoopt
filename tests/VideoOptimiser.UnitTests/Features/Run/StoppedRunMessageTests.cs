using FluentAssertions;
using VideoOptimiser.Cli.Features.Run;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.UnitTests.Features.Run;

public sealed class StoppedRunMessageTests
{
    /// <summary>An <c>immich run</c> is resumed by <c>run</c>: what it queued is in the queue, and <c>run</c> converts the queue.</summary>
    [Theory]
    [InlineData(new[] { "run", "--remote", "--config", @"C:\vo\my.yaml" }, @"run --remote --config ""C:\vo\my.yaml""")]
    [InlineData(new[] { "immich", "run" }, "run")]
    [InlineData(new[] { "--first", "immich", "run", "--local", "--config", "my.yaml" }, @"run --local --config ""my.yaml""")]
    [InlineData(new[] { "immich", "run", "--limit", "3" }, "run")]
    public void TheRerunCommandIsRunWithTheModeAndConfigTheUserTyped(string[] args, string expected)
    {
        StoppedRunMessage.RerunCommand(CliCommand.Parse(args)).Should().Be(expected);
    }

    /// <summary>
    /// The finished encode exists only on the worker until it is downloaded, so deleting the worker loses it; a stopped upload
    /// leaves nothing running there, but the worker stays up.
    /// </summary>
    [Theory]
    [InlineData(JobStatus.Downloading, new[]
    {
        "Stopped. The encoded output is waiting on the worker w, which stays up, billed hourly.",
        "To resume: video-optimiser run",
        "To delete it instead: video-optimiser worker delete (the finished encode is lost)"
    })]
    [InlineData(JobStatus.Staging, new[]
    {
        "Stopped. Nothing is running on the worker w, but it stays up, billed hourly, so the job can resume.",
        "To resume: video-optimiser run",
        "To delete it instead: video-optimiser worker delete"
    })]
    public void AStoppedTransferSaysWhatDeletingTheWorkerLoses(JobStatus stage, string[] expected)
    {
        StoppedRunMessage.Lines(Stopped(stage), "w", managed: true, CliCommand.Parse(["run"])).Should().Equal(expected);
    }

    [Theory]
    [InlineData(JobStatus.CrfSearching, true, "Stopped watching. The CRF search of clip.mov is still running on the worker w.")]
    [InlineData(JobStatus.Encoding, false, "Stopped. Nothing is running on the worker w, but it stays up, billed hourly, so the job can resume.")]
    public void TheFirstLineNamesWhatIsLeftOnTheWorker(JobStatus stage, bool remote, string expected)
    {
        StoppedRunMessage.Lines(Stopped(stage, remote), "w", managed: true, CliCommand.Parse(["run"]))[0].Should().Be(expected);
    }

    /// <summary>A manual host is neither billed nor deleted by this tool, so it hears only what is running and how to pick it up.</summary>
    [Theory]
    [InlineData(JobStatus.Encoding, new[] { "Stopped watching. The encode of clip.mov is still running on the worker my-host.", "To pick it up again: video-optimiser run" })]
    [InlineData(JobStatus.Staging, new string[0])]
    public void AManualHostGetsOnlyTheLinesThatHoldForIt(JobStatus stage, string[] expected)
    {
        StoppedRunMessage.Lines(Stopped(stage), "my-host", managed: false, CliCommand.Parse(["run"])).Should().Equal(expected);
    }

    private static JobInterruptedException Stopped(JobStatus stage, bool remote = true) =>
        new(new JobRecord { SourcePath = @"D:\Videos\clip.mov" }, stage, remote, CancellationToken.None);
}
