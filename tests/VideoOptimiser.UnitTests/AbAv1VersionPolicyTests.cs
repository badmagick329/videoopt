using FluentAssertions;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class AbAv1VersionPolicyTests
{
    [Theory]
    [InlineData("0.11.7", "0.11.7")]
    [InlineData("  ab-av1 v0.12.1  ", "0.12.1")]
    public void TryParseAcceptsBareVersionsAndTheRawVersionLine(string value, string expected)
    {
        AbAv1VersionPolicy.TryParse(value, out var version).Should().BeTrue();
        version.Should().Be(Version.Parse(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ab-av1 0.11")]
    [InlineData("ab-av1 0.11.7-beta")]
    [InlineData("ffmpeg 0.11.7")]
    public void TryParseRejectsAnythingElse(string? value) =>
        AbAv1VersionPolicy.TryParse(value, out _).Should().BeFalse();
}
