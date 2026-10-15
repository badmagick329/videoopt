using FluentAssertions;
using VideoOptimiser.Application.Features.Cleanup;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Cli.Features.Cleanup;

namespace VideoOptimiser.UnitTests.Features.Cleanup;

/// <summary>What <c>cleanup</c> lists and totals, rendered by a small class in the Cli so the layout is tested without the command.</summary>
public sealed class CleanupReportTests
{
    [Fact]
    public void TheCleanupTotalNamesOnlyTheKindsOfFileFound()
    {
        var folder = Path.Combine(Path.GetTempPath(), "videos");
        var abandoned = new AbandonedTemporaryOutput(Path.Combine(folder, "a.encoding.mp4"), Guid.NewGuid(), 1, 3L << 20, "job failed");
        var staged = new StagedImmichOriginal(Path.Combine(folder, "PXL.mp4"), Guid.NewGuid(), 500L << 20, "job completed");

        CleanupReport.Summary(new TemporaryOutputCleanupPlan([abandoned], [], [])).Should().Be("Abandoned temporary outputs: 1. Total: 3.0 MiB.");
        CleanupReport.Summary(new TemporaryOutputCleanupPlan([], [staged], [])).Should().Be("Staged Immich originals: 1. Total: 500.0 MiB.");
        CleanupReport.Summary(new TemporaryOutputCleanupPlan([abandoned], [staged], [])).Should().Be("Abandoned temporary outputs: 1. Staged Immich originals: 1. Total: 503.0 MiB.");
    }

    [Fact]
    public void CleanupPrintsEachFolderOnceWithItsFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "videos", ".video-optimiser");
        var staging = Path.Combine(Path.GetTempPath(), "staging", "0f1e");
        var output = new StringWriter { NewLine = "\n" };
        var plan = new TemporaryOutputCleanupPlan(
            [new AbandonedTemporaryOutput(Path.Combine(folder, "a.encoding.mp4"), Guid.NewGuid(), 1, 3L << 20, "job failed"), new AbandonedTemporaryOutput(Path.Combine(folder, "b.encoding.mp4"), Guid.NewGuid(), 2, 1L << 30, "job cancelled")],
            [new StagedImmichOriginal(Path.Combine(staging, "PXL.mp4"), Guid.NewGuid(), 500L << 20, "job completed")],
            []);

        CleanupReport.WriteFiles(output, plan);

        output.ToString().Should().Be(
            $"{folder}\n" +
            "     3.0 MiB  a.encoding.mp4 (job failed)\n" +
            "     1.0 GiB  b.encoding.mp4 (job cancelled)\n" +
            $"{staging}\n" +
            "   500.0 MiB  PXL.mp4 (job completed)\n");
    }
}
