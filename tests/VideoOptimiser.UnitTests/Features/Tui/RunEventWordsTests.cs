using FluentAssertions;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Cli.Features.Tui;

namespace VideoOptimiser.UnitTests.Features.Tui;

public sealed class RunEventWordsTests
{
    /// <summary>The TUI's summary counts warned videos within the ready ones, as the console's does (VOPT-117).</summary>
    [Theory]
    [InlineData(0, "The run finished: 4 ready to finalise.")]
    [InlineData(1, "The run finished: 4 ready to finalise (1 to check).")]
    public void TheSummaryCountsVideosToCheckWithinTheReadyOnes(int toCheck, string expected)
    {
        var effect = RunEventWords.Of(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.Summary, Ready: 4, Skipped: 0, Failures: [], ToCheck: toCheck));

        effect.Lines.Should().ContainSingle().Which.Text.Should().Be(expected);
    }
}
