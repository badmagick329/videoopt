using FluentAssertions;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class AbAv1VersionPolicyTests
{
    [Theory]
    [InlineData("0.11.7", "0.11.7")]
    [InlineData("  ab-av1 v0.12.1  ", "0.12.1")]
    [InlineData(null, null)]
    [InlineData("ab-av1 0.11", null)]
    [InlineData("ab-av1 0.11.7-beta", null)]
    [InlineData("ffmpeg 0.11.7", null)] // another tool's version is never taken for ab-av1's
    public void TryParseAcceptsBareVersionsAndTheRawVersionLineAndNothingElse(string? value, string? expected)
    {
        AbAv1VersionPolicy.TryParse(value, out var version).Should().Be(expected is not null);
        if (expected is not null) version.Should().Be(Version.Parse(expected));
    }
}
