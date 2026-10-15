using FluentAssertions;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli;
using VideoOptimiser.Cli.Features.Run;

namespace VideoOptimiser.UnitTests.Features.Run;

public sealed class JobProgressConsoleTests
{
    [Fact]
    public void ALineIsIndentedByItsKindAndAHeaderOpensWithABlankLine()
    {
        var output = new StringWriter { NewLine = "\n" };
        var console = new JobProgressConsole(output, TerminalStyle.Plain);

        console.Report(new JobProgress(JobProgressKind.Header, "[1/2] a.mp4 (1.0 MiB)"));
        console.Report(JobProgress.Step("Uploading 1.0 MiB to the worker..."));
        console.Report(JobProgress.Detail("41% of 1.0 MiB"));
        console.Report(JobProgress.Tool("a line the translator did not recognise"));
        console.Report(new JobProgress(JobProgressKind.Header, "[2/2] b.mp4"));

        output.ToString().Should().Be(
            "\n[1/2] a.mp4 (1.0 MiB)\n" +
            "      Uploading 1.0 MiB to the worker...\n" +
            "        41% of 1.0 MiB\n" +
            "        a line the translator did not recognise\n" +
            "\n[2/2] b.mp4\n");
    }

    [Fact]
    public void HeaderNamesTheFileAndItsSizeAndOmitsTheSizeOfAMissingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Header.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var present = Path.Combine(directory, "PXL_1.mp4");
            File.WriteAllBytes(present, new byte[3 * 1024 * 1024]);

            JobProgress.Header(2, 5, present).Should().Be(new JobProgress(JobProgressKind.Header, "[2/5] PXL_1.mp4 (3.0 MiB)"));
            JobProgress.Header(5, 5, Path.Combine(directory, "gone.mp4")).Should().Be(new JobProgress(JobProgressKind.Header, "[5/5] gone.mp4"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
