using FluentAssertions;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class OutputContainerTests
{
    [Theory]
    [InlineData("clip.mp4", ".mp4")]
    [InlineData("clip.mov", ".mp4")]
    [InlineData("clip.m4v", ".mp4")]
    [InlineData("clip.mkv", ".mkv")]
    [InlineData("clip", ".mkv")]
    public void TheContainerFollowsWhatTheCodecAndMuxerAccept(string source, string expected) =>
        OutputContainer.ExtensionFor(source).Should().Be(expected);

    [Theory]
    [InlineData("IMG_0897.MOV", ".MP4")]
    [InlineData("IMG_0897.AVI", ".MKV")]
    public void AnAllCapitalsExtensionStaysInCapitals(string source, string expected) =>
        OutputContainer.ExtensionFor(source).Should().Be(expected);

    [Theory]
    [InlineData("clip.Mov", ".mp4")]
    public void AMixedCaseExtensionBecomesLowercase(string source, string expected) =>
        OutputContainer.ExtensionFor(source).Should().Be(expected);
}
