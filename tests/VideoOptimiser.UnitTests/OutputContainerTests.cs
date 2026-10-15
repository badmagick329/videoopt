using FluentAssertions;
using VideoOptimiser.Application.TemporaryOutputs;

namespace VideoOptimiser.UnitTests;

public sealed class OutputContainerTests
{
    [Theory]
    [InlineData("clip.mp4", ".mp4")]
    [InlineData("clip.mov", ".mp4")]
    [InlineData("clip.m4v", ".mp4")]
    [InlineData("clip.mkv", ".mkv")]
    [InlineData("clip", ".mkv")]
    [InlineData("IMG_0897.MOV", ".MP4")] // an all-capitals extension stays in capitals
    [InlineData("clip.Mov", ".mp4")] // a mixed-case one becomes lowercase
    public void TheContainerFollowsWhatTheCodecAndMuxerAccept(string source, string expected) =>
        OutputContainer.ExtensionFor(source).Should().Be(expected);
}
