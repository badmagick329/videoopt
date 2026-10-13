using FluentAssertions;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class CrfSampleCountCalculatorTests
{
    [Theory]
    [InlineData(120, 2)]
    [InlineData(240, 2)]
    [InlineData(241, 3)]
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

    [Theory]
    [InlineData(8d, 2, 20, true)]
    [InlineData(47.05, 2, 20, true)]
    [InlineData(47.1, 2, 20, false)]
    [InlineData(600d, 2, 0, true)]
    [InlineData(null, 2, 20, false)]
    public void TestsTheWholeVideoWhereAbAv1Would(double? durationSeconds, int sampleCount, int sampleSeconds, bool expected)
    {
        CrfSampleCountCalculator.TestsWholeVideo(durationSeconds, sampleCount, TimeSpan.FromSeconds(sampleSeconds)).Should().Be(expected);
    }
}
