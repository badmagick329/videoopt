using FluentAssertions;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class CrfSampleCountCalculatorTests
{
    /// <summary>Unset, the duration picks one sample per two minutes, 2 to 12; a set count wins over the duration either way.</summary>
    [Theory]
    [InlineData(120d, null, 2)]
    [InlineData(240d, null, 2)]
    [InlineData(241d, null, 3)]
    [InlineData(1680d, null, 12)]
    [InlineData(null, null, 5)]
    [InlineData(1680d, 3, 3)]
    [InlineData(null, 3, 3)]
    public void CalculatesSampleCount(double? durationSeconds, int? configured, int expected)
    {
        CrfSampleCountCalculator.Calculate(durationSeconds, configured).Should().Be(expected);
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
