using FluentAssertions;
using VideoOptimiser.Application.Features.Run;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// <see cref="RunStop.Withdraw"/> (VOPT-187). What a press does is <see cref="CtrlCRunTests"/>' through the real CLI; this is
/// only the way back from a stop after this video, and that a stop now has none.
/// </summary>
public sealed class RunStopTests
{
    /// <summary>After a withdraw the stop is as if never pressed: the next press stops after this video again, not now.</summary>
    [Fact]
    public void AWithdrawnStopAfterThisVideoCanBeAskedAgain()
    {
        using var stop = new RunStop();
        var stoppingAfterThisVideo = 0;
        using var converting = stop.WhileConverting(() => stoppingAfterThisVideo++);
        stop.Press();

        stop.Withdraw().Should().BeTrue();
        stop.StopsAfterThisVideo.Should().BeFalse();
        stop.Withdraw().Should().BeFalse("there is nothing left to withdraw");

        stop.Press();
        stop.StopsAfterThisVideo.Should().BeTrue();
        stoppingAfterThisVideo.Should().Be(2);
        stop.Now.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void AStopNowCantBeWithdrawn()
    {
        using var stop = new RunStop();
        using var converting = stop.WhileConverting(() => { });
        stop.Press();
        stop.StopNow();

        stop.Withdraw().Should().BeFalse();
        stop.Now.IsCancellationRequested.Should().BeTrue();
        stop.StopsAfterThisVideo.Should().BeTrue();
    }
}
