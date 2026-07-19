using FluentAssertions;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class CrfSampleCountCalculatorTests
{
    [Theory]
    [InlineData(120, 2)]
    [InlineData(121, 2)]
    [InlineData(240, 2)]
    [InlineData(241, 3)]
    [InlineData(420, 4)]
    [InlineData(600, 5)]
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
