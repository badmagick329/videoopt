using FluentAssertions;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class CrfSampleCountCalculatorTests
{
    [Theory]
    [InlineData(120, 2)]
    [InlineData(121, 3)]
    [InlineData(180, 3)]
    [InlineData(240, 4)]
    [InlineData(241, 5)]
    [InlineData(420, 5)]
    [InlineData(600, 6)]
    [InlineData(1680, 12)]
    public void CalculatesSampleCountFromVideoDuration(double durationSeconds, int expected)
    {
        CrfSampleCountCalculator.Calculate(durationSeconds, fallbackSampleCount: 5).Should().Be(expected);
    }

    [Fact]
    public void UsesConfiguredCountWhenDurationIsUnavailable()
    {
        CrfSampleCountCalculator.Calculate(null, fallbackSampleCount: 5).Should().Be(5);
    }
}
