using FluentAssertions;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests;

public sealed class HumanReadableValuesTests
{
    [Theory]
    [InlineData("2GiB", 2147483648L)]
    [InlineData("100MB", 100000000L)]
    [InlineData("500 MiB", 524288000L)]
    public void TryParseSizeParsesDecimalAndBinaryUnits(string value, long expected)
    {
        HumanReadableValues.TryParseSize(value, out var actual).Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1GiB")]
    [InlineData("12XB")]
    public void TryParseSizeRejectsInvalidValues(string value)
    {
        HumanReadableValues.TryParseSize(value, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("8Mbps", 8_000_000L)]
    [InlineData("500 Kbps", 500_000L)]
    [InlineData("1.5Gbps", 1_500_000_000L)]
    public void TryParseBitrateParsesSupportedUnits(string value, long expected)
    {
        HumanReadableValues.TryParseBitrate(value, out var actual).Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0Mbps")]
    [InlineData("8MB/s")]
    [InlineData("8bps")]
    public void TryParseBitrateRejectsMalformedOrZeroValues(string value) =>
        HumanReadableValues.TryParseBitrate(value, out _).Should().BeFalse();

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
