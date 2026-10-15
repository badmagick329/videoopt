using FluentAssertions;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// One row per unit or rejection branch. The other units and every size band are pinned where they are used:
/// <c>EligibilityEvaluatorTests</c> (<c>2GiB</c> and <c>2048MiB</c> are one fingerprint, <c>15000kbps</c> and <c>15Mbps</c> too)
/// and the transcripts that print sizes.
/// </summary>
public sealed class HumanReadableValuesTests
{
    [Theory]
    [InlineData("100MB", 100000000L)]
    [InlineData("", null)]
    [InlineData("-1GiB", null)]
    public void TryParseSizeParsesDecimalUnitsAndRejectsInvalidValues(string value, long? expected)
    {
        HumanReadableValues.TryParseSize(value, out var actual).Should().Be(expected is not null);
        if (expected is not null) actual.Should().Be(expected);
    }

    [Fact]
    public void FormatSizeTextLeavesTextThatIsNotASizeAlone() =>
        HumanReadableValues.FormatSizeText("not a size").Should().Be("not a size");

    [Theory]
    [InlineData("1.5Gbps", 1_500_000_000L)]
    [InlineData("", null)]
    [InlineData("0Mbps", null)]
    [InlineData("8MB/s", null)]
    public void TryParseBitrateParsesGigabitsAndRejectsMalformedOrZeroValues(string value, long? expected)
    {
        HumanReadableValues.TryParseBitrate(value, out var actual).Should().Be(expected is not null);
        if (expected is not null) actual.Should().Be(expected);
    }

    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("2m", 120000)]
    [InlineData("1d", 86400000)]
    public void TryParseDurationParsesSupportedUnits(string value, double expectedMilliseconds)
    {
        HumanReadableValues.TryParseDuration(value, out var duration).Should().BeTrue();
        duration.TotalMilliseconds.Should().Be(expectedMilliseconds);
    }
}
