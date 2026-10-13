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
    [InlineData("clip.avi", ".mkv")]
    [InlineData("clip.ts", ".mkv")]
    [InlineData("clip.webm", ".mkv")]
    [InlineData("clip.xyz", ".mkv")]
    [InlineData("clip", ".mkv")]
    public void TheContainerFollowsWhatTheCodecAndMuxerAccept(string source, string expected) =>
        OutputContainer.ExtensionFor(source).Should().Be(expected);

    [Theory]
    [InlineData("IMG_0897.MOV", ".MP4")]
    [InlineData("IMG_0897.MP4", ".MP4")]
    [InlineData("IMG_0897.M4V", ".MP4")]
    [InlineData("IMG_0897.AVI", ".MKV")]
    public void AnAllCapitalsExtensionStaysInCapitals(string source, string expected) =>
        OutputContainer.ExtensionFor(source).Should().Be(expected);

    [Theory]
    [InlineData("clip.Mov", ".mp4")]
    [InlineData("clip.mOV", ".mp4")]
    [InlineData("clip.Avi", ".mkv")]
    public void AMixedCaseExtensionBecomesLowercase(string source, string expected) =>
        OutputContainer.ExtensionFor(source).Should().Be(expected);

    [Fact]
    public void OnlyTheExtensionDecidesNotTheFolderOrTheStem() =>
        OutputContainer.ExtensionFor(@"C:\v\holiday.mov\clip.mkv").Should().Be(".mkv");
}
