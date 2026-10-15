using FluentAssertions;
using VideoOptimiser.Application.Features.Run;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>Every way a run ends decides the managed worker's fate (VOPT-54 step 3). One row per branch.</summary>
public sealed class WorkerEndPolicyTests
{
    [Theory]
    // Finished: deleteAfterRun decides, failures or not, but only for a worker this run used.
    [InlineData(RunEnd.Finished, null, true, true, false, true, WorkerFate.Delete)]
    [InlineData(RunEnd.Finished, null, false, true, false, true, WorkerFate.Keep)]
    [InlineData(RunEnd.Finished, null, true, false, false, true, WorkerFate.Keep)]
    // An answer beats deleteAfterRun.
    [InlineData(RunEnd.Finished, false, true, true, false, true, WorkerFate.Keep)]
    [InlineData(RunEnd.StoppedAfterVideo, true, false, true, false, true, WorkerFate.Delete)]
    // Stopped with no answer: asked when input is interactive, kept otherwise.
    [InlineData(RunEnd.StoppedAfterVideo, null, true, true, false, true, WorkerFate.Ask)]
    [InlineData(RunEnd.StoppedNow, null, true, true, false, false, WorkerFate.Keep)]
    // A y before the second press is asked again when deleting would lose work.
    [InlineData(RunEnd.StoppedNow, true, false, true, true, true, WorkerFate.Ask)]
    [InlineData(RunEnd.StoppedNow, true, false, true, false, true, WorkerFate.Delete)]
    // The worker stopped answering: kept unless the user said y; the delete goes through the Hetzner API.
    [InlineData(RunEnd.WorkerUnreachable, null, true, true, true, true, WorkerFate.Keep)]
    [InlineData(RunEnd.WorkerUnreachable, true, false, true, true, true, WorkerFate.Delete)]
    [InlineData(RunEnd.Failed, null, true, true, false, true, WorkerFate.Keep)]
    public void EachEndDecidesTheWorkersFate(RunEnd end, bool? answer, bool deleteAfterRun, bool workerUsed, bool deleteLosesWork, bool canAsk, WorkerFate expected) =>
        WorkerEndPolicy.Decide(end, answer, deleteAfterRun, workerUsed, deleteLosesWork, canAsk).Should().Be(expected);

    [Theory]
    [InlineData("y", true)]
    [InlineData(" Yes ", true)]
    [InlineData("n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("delete", false)]
    public void OnlyAnExplicitYesCounts(string? response, bool expected) => WorkerEndPolicy.IsAffirmative(response).Should().Be(expected);
}
