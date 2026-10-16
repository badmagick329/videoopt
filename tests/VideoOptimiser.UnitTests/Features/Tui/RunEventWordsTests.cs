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

    [Theory]
    [InlineData(1, "Checking the AV1 of 1 video that failed validation earlier.")]
    [InlineData(3, "Checking the AV1s of 3 videos that failed validation earlier.")]
    public void TheRecheckSaysHowManyVideosItChecks(int count, string expected)
    {
        var effect = RunEventWords.Of(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.RecheckStarted, Count: count));

        effect.Lines.Should().ContainSingle().Which.Text.Should().Be(expected);
    }

    /// <summary>A part whose count is zero is left out, and a video still failed turns the line into a warning (VOPT-119).</summary>
    [Theory]
    [InlineData(2, 0, 0, 0, "Checked again: 2 ready to finalise.", false)]
    [InlineData(2, 1, 0, 0, "Checked again: 2 ready to finalise (1 to check).", false)]
    [InlineData(0, 0, 1, 0, "Checked again: 1 not worth converting.", false)]
    [InlineData(1, 1, 0, 2, "Checked again: 1 ready to finalise (1 to check), 2 still failed.", true)]
    public void TheRecheckTallyNamesWhatTheVideosBecame(int ready, int toCheck, int notWorthConverting, int stillFailed, string expected, bool warns)
    {
        var effect = RunEventWords.Of(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.Rechecked, Count: stillFailed, Ready: ready, Skipped: notWorthConverting, ToCheck: toCheck));

        effect.Lines.Should().ContainSingle().Which.Should().Match<LogEntry>(line => line.Text == expected && line.Kind == (warns ? LogKind.Warning : LogKind.Success));
    }
}
