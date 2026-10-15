using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Finalize;

namespace VideoOptimiser.UnitTests.Features.Finalize;

public sealed class FinalizationMessageTests
{
    private static readonly FileInstallation Mp4 = new(@"D:\v\clip.mp4", null);

    [Fact]
    public void DescribeNamesTheAv1WhenADeletedOriginalHadAnotherExtension() =>
        FinalizationService.Describe(@"D:\v\clip.mov", Action(OriginalActions.Delete), Mp4)
            .Should().Be(@"Original deleted; the AV1 is D:\v\clip.mp4.");

    [Fact]
    public void DescribeSaysWhereTheOriginalWasArchivedAndOnlyNamesTheAv1WhenItsPathChanged()
    {
        var archived = new FileInstallation(@"D:\v\clip.mp4", @"D:\v\Archive\clip.mp4");
        FinalizationService.Describe(@"D:\v\clip.mp4", Action(OriginalActions.Archive), archived)
            .Should().Be(@"Original archived to D:\v\Archive\clip.mp4.");

        var renamed = new FileInstallation(@"D:\v\clip.mp4", @"D:\v\Archive\clip.mov");
        FinalizationService.Describe(@"D:\v\clip.mov", Action(OriginalActions.Archive), renamed)
            .Should().Be(@"Original archived to D:\v\Archive\clip.mov; the AV1 is D:\v\clip.mp4.");
    }

    [Fact]
    public void DescribeSaysKeepWhateverTheExtension()
    {
        var kept = new FileInstallation(@"D:\v\clip.av1.mp4", null);
        FinalizationService.Describe(@"D:\v\clip.mp4", Action(OriginalActions.Keep), kept).Should().Be(@"Original kept; AV1 installed as D:\v\clip.av1.mp4.");
        FinalizationService.Describe(@"D:\v\clip.mov", Action(OriginalActions.Keep), kept).Should().Be(@"Original kept; AV1 installed as D:\v\clip.av1.mp4.");
    }

    private static OriginalSettings Action(string action) => new() { Action = action };
}
