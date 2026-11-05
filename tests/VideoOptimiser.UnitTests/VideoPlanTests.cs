using FluentAssertions;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class VideoPlanTests
{
    private static readonly FieldCounts Combed = new(495, 0, 5, 0);

    private static MediaInfo Media(string? fieldOrder, double duration = 120, double? frameRate = 30000.0 / 1001) =>
        new("h264", 1, 1, 0, 0, duration, null, 1920, 1080, null, null, fieldOrder, frameRate);

    /// <summary>Any interlaced flag lets idet decide, and the parity is the one idet sees: the <c>tb</c> row is x264 woven top
    /// field first and muxed to Matroska. A stream not flagged interlaced is left alone however combed its frames look.</summary>
    [Theory]
    [InlineData("tt", 495, 0, "bwdif=mode=send_field:parity=tff:deint=all")]
    [InlineData("tb", 495, 0, "bwdif=mode=send_field:parity=tff:deint=all")]
    [InlineData("bb", 0, 495, "bwdif=mode=send_field:parity=bff:deint=all")]
    [InlineData("progressive", 495, 0, null)]
    [InlineData(null, 495, 0, null)]
    public void TheParityIsTheOneIdetSees(string? fieldOrder, int topFirst, int bottomFirst, string? filter) =>
        VideoPlan.For(Media(fieldOrder), new FieldCounts(topFirst, bottomFirst, 5, 0)).Filter.Should().Be(filter);

    /// <summary>The threshold's two sides, from the survey: a misflagged progressive video scored at most 15% on either parity,
    /// an interlaced one with one clean frame in five about 80% on one. Undetermined frames don't count either way.</summary>
    [Theory]
    [InlineData(72, 52, 377, 0, false)]
    [InlineData(200, 200, 100, 0, false)]
    [InlineData(400, 0, 100, 900, true)]
    [InlineData(0, 0, 0, 500, false)]
    public void AFlaggedVideoIsDeinterlacedOnlyWhenMostOfItsFramesLookCombedTheSameWay(int topFirst, int bottomFirst, int progressive, int undetermined, bool deinterlaced) =>
        (VideoPlan.For(Media("tt"), new FieldCounts(topFirst, bottomFirst, progressive, undetermined)).Filter is not null).Should().Be(deinterlaced);

    /// <summary>ab-av1 counts its default 10 s keyframe interval in input frames, only on a video over 3 minutes; deinterlacing
    /// doubles the frames, so the interval is given in output frames.</summary>
    [Theory]
    [InlineData(181, 599)]
    [InlineData(180, null)]
    public void ALongVideoKeepsKeyframesTenSecondsApart(double duration, int? keyframes) =>
        VideoPlan.For(Media("tt", duration), Combed).KeyframeInterval.Should().Be(keyframes);

    [Fact]
    public void TheNoteNamesTheScanBeforeAndAfter() =>
        VideoPlan.For(Media("tt"), Combed).Note.Should().Be("Deinterlaced from 1080i29.97 to 1080p59.94.");
}
