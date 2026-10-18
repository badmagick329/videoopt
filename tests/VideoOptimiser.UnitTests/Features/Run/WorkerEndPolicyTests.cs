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
        WorkerEndPolicy.Decide(RunEnd.StoppedAfterVideo, answer: null, deleteAfterRun: true, workerUsed: true, deleteLosesWork: false, canAsk: true, createdByRun: false, workerUnusable: false).Should().Be(WorkerFate.Ask);

    /// <summary>
    /// A failed run keeps a worker it never created, or one it used, for the user to inspect. A worker it created that never
    /// became usable, or one that never can, is deleted unless someone is there to ask; with nobody there, a worker that never
    /// can is deleted even against a background run's Keep (VOPT-140).
    /// </summary>
    [Theory]
    [InlineData(null, true, false, true, false, WorkerFate.Ask)]
    [InlineData(null, true, false, false, false, WorkerFate.Delete)]
    [InlineData(null, false, false, false, false, WorkerFate.Keep)]
    [InlineData(null, true, true, false, false, WorkerFate.Keep)]
    [InlineData(null, false, false, true, true, WorkerFate.Ask)]
    [InlineData(false, false, false, false, true, WorkerFate.Delete)]
    [InlineData(false, false, false, true, true, WorkerFate.Keep)]
    public void AFailedRunDeletesAWorkerThatNeverBecameUsableOnlyWhenNobodyCanBeAsked(bool? answer, bool createdByRun, bool workerUsed, bool canAsk, bool workerUnusable, WorkerFate expected) =>
        WorkerEndPolicy.Decide(RunEnd.Failed, answer, deleteAfterRun: false, workerUsed, deleteLosesWork: false, canAsk, createdByRun, workerUnusable).Should().Be(expected);

    [Fact]
    public void AYesCountsWhateverItsCaseAndSpacing() => WorkerEndPolicy.IsAffirmative(" Yes ").Should().BeTrue();
}
