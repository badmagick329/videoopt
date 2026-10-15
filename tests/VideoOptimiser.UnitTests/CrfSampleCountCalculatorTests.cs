using FluentAssertions;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class CrfSampleCountCalculatorTests
{
    [Theory]
    [InlineData(120d, 2)]
    [InlineData(240d, 2)]
    [InlineData(241d, 3)]
    [InlineData(1680d, 12)]
    [InlineData(null, 5)] // no duration: the configured count of 5
    public void CalculatesSampleCountFromVideoDuration(double? durationSeconds, int expected)
    {
        CrfSampleCountCalculator.Calculate(durationSeconds, fallbackSampleCount: 5).Should().Be(expected);
    }

    [Theory]
    [InlineData(47.05, 2, 20, true)]
    [InlineData(47.1, 2, 20, false)]
    [InlineData(600d, 2, 0, true)]
    [InlineData(null, 2, 20, false)]
    public void TestsTheWholeVideoWhereAbAv1Would(double? durationSeconds, int sampleCount, int sampleSeconds, bool expected)
    {
        CrfSampleCountCalculator.TestsWholeVideo(durationSeconds, sampleCount, TimeSpan.FromSeconds(sampleSeconds)).Should().Be(expected);
    }
}
