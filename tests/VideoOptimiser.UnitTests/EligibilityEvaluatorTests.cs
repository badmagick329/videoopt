using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Scanning;

namespace VideoOptimiser.UnitTests;

public sealed class EligibilityEvaluatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public EligibilityEvaluatorTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("1080p-1440p", 1920, 1080, true)]
    [InlineData("1080p-1440p", 2560, 1440, false)]
    [InlineData("1440p-4k", 2560, 1440, true)]
    [InlineData("1440p-4k", 3840, 2160, false)]
    [InlineData("4k+", 3840, 2160, true)]
    public void IsEligibleUsesPixelCountResolutionBands(string resolution, int width, int height, bool expected)
    {
        var eligible = IsEligible(new EligibilityRuleSettings { Codecs = ["h264"], Resolution = resolution, MinimumVideoBitrate = "8Mbps", MinimumFileSize = "1B" }, Media(width: width, height: height));

        eligible.Should().Be(expected);
    }

    [Fact]
    public void IsEligibleRequiresEveryCriterionWithinARule()
    {
        var rule = new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "8Mbps", MinimumFileSize = "10B" };

        IsEligible(rule, Media(codec: "hevc")).Should().BeFalse();
        IsEligible(rule, Media(bitrate: 7_999_999)).Should().BeFalse();
        IsEligible(rule, Media(width: 1280, height: 720)).Should().BeFalse();

        var path = CreateFile(9);
        EligibilityEvaluator.IsEligible(new FileInfo(path), Media(), new EligibilitySettings { Rules = [rule] }, out var reason).Should().BeFalse();
        reason.Should().Contain("size is below 10B");
    }

    [Fact]
    public void IsEligibleMatchesAnyRule()
    {
        var settings = new EligibilitySettings
        {
            Rules =
            [
                new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "4k+", MinimumVideoBitrate = "20Mbps", MinimumFileSize = "1B" },
                new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "8Mbps", MinimumFileSize = "1B" }
            ]
        };
        var path = CreateFile(1);

        EligibilityEvaluator.IsEligible(new FileInfo(path), Media(), settings, out _).Should().BeTrue();
    }

    [Fact]
    public void IsEligibleDoesNotMatchWhenVideoBitrateIsMissing()
    {
        IsEligible(new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "8Mbps", MinimumFileSize = "1B" }, Media(bitrate: null)).Should().BeFalse();
    }

    [Theory]
    [InlineData("1080p-1440p", true)]
    [InlineData("1440p-4k", true)]
    [InlineData("4k+", true)]
    [InlineData("1080p", false)]
    [InlineData("4K+", true)]
    public void IsValidResolutionBandRecognisesSupportedValues(string value, bool expected) =>
        EligibilityEvaluator.IsValidResolutionBand(value).Should().Be(expected);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private bool IsEligible(EligibilityRuleSettings rule, MediaInfo media)
    {
        var path = CreateFile(10);
        return EligibilityEvaluator.IsEligible(new FileInfo(path), media, new EligibilitySettings { Rules = [rule] }, out _);
    }

    private string CreateFile(int length)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private static MediaInfo Media(string codec = "h264", int width = 1920, int height = 1080, long? bitrate = 8_000_000) =>
        new(codec, 1, 0, 0, 0, 0, 0, width, height, bitrate);
}
