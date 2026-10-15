using FluentAssertions;
using VideoOptimiser.Application.Features.Run;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// Every way a run ends decides the managed worker's fate (VOPT-54 step 3). The run transcripts in <see cref="WorkerEndRunTests"/>
/// and <see cref="UnreachableWorkerRunTests"/> drive the other branches; these are the ones no transcript reaches.
/// </summary>
public sealed class WorkerEndPolicyTests
{
    /// <summary>The only transcript that reaches this end goes through the pending answer, which bypasses the policy.</summary>
    [Fact]
    public void AStopAfterAVideoWithNoAnswerAsksWhenInputIsInteractive() =>
        WorkerEndPolicy.Decide(RunEnd.StoppedAfterVideo, answer: null, deleteAfterRun: true, workerUsed: true, deleteLosesWork: false, canAsk: true).Should().Be(WorkerFate.Ask);

    [Fact]
    public void AYesCountsWhateverItsCaseAndSpacing() => WorkerEndPolicy.IsAffirmative(" Yes ").Should().BeTrue();
}
