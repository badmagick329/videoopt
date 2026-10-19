using FluentAssertions;
using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.UnitTests.Features.Finalize;

/// <summary>
/// What a finalise chooses from the open jobs. Which videos each command takes is pinned through the commands
/// (<c>FinalizeCommandsTests</c>, <c>TuiActionTests</c>); these pin what they only see through the plan.
/// </summary>
public sealed class FinalizationPlanTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Both lists follow the run order, whatever order the jobs come in: the repository lists by last update, which a
    /// finalisation that returned a video to ready would reshuffle.
    /// </summary>
    [Fact]
    public void BothListsAreInRunOrderWhateverOrderTheJobsComeIn()
    {
        var older = Ready(minutes: 0);
        var newer = Ready(minutes: 5);
        var movedNext = Ready(minutes: 9, priority: 1);
        var warnedOlder = Warned(Ready(minutes: 1));
        var warnedNewer = Warned(Ready(minutes: 7));

        var plan = FinalizationPlan.Bulk([newer, warnedNewer, older, movedNext, warnedOlder]);

        plan.Ready.Should().Equal(movedNext, older, newer);
        plan.LeftOut.Should().Equal(warnedOlder, warnedNewer);
    }

    /// <summary>A warned video is left out only while it is ready: one that is not finalisable is in neither list.</summary>
    [Theory]
    [InlineData(JobStatus.ReadyToFinalize, true)]
    [InlineData(JobStatus.Queued, false)]
    [InlineData(JobStatus.Failed, false)]
    public void AWarnedVideoIsLeftOutOnlyWhileItIsReady(JobStatus status, bool leftOut)
    {
        var job = Warned(Ready(minutes: 0));
        job.Status = status;

        var plan = FinalizationPlan.Bulk([job]);

        plan.Ready.Should().BeEmpty();
        plan.LeftOut.Should().HaveCount(leftOut ? 1 : 0);
        plan.IsEmpty.Should().Be(!leftOut);
    }

    private static JobRecord Ready(int minutes, int priority = 0) => new()
    {
        Id = Guid.NewGuid(),
        SourcePath = $@"C:\videos\clip-{minutes}.mp4",
        Status = JobStatus.ReadyToFinalize,
        OutputPath = $@"C:\videos\.video-optimiser\clip-{minutes}.encoding.mp4",
        CreatedUtc = Start.AddMinutes(minutes),
        Priority = priority
    };

    private static JobRecord Warned(JobRecord job)
    {
        job.ValidationWarnings = ["The AV1 has no audio."];
        return job;
    }
}
