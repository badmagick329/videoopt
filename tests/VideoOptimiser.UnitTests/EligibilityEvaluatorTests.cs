using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Tools;

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

    [Fact]
    public void RulesFingerprintIgnoresWhatCannotChangeAVerdict()
    {
        var original = new EligibilitySettings { Rules = [Rule(["h264"], "1080p-1440p", "15Mbps", "800MiB"), Rule(["hevc", "h264"], "4k+", "35Mbps", "2GiB")] };
        var reordered = new EligibilitySettings { Rules = [Rule(["H264", "HEVC"], "4K+", "35 Mbps", "2048MiB"), Rule(["h264"], "1080p-1440p", "15000kbps", "800 MiB")] };

        EligibilityEvaluator.RulesFingerprint(reordered).Should().Be(EligibilityEvaluator.RulesFingerprint(original));
    }

    [Fact]
    public void RulesFingerprintChangesWithAnyThresholdOrCodec()
    {
        var baseline = EligibilityEvaluator.RulesFingerprint(new EligibilitySettings { Rules = [Rule(["h264"], "1080p-1440p", "15Mbps", "800MiB")] });

        string[] changed =
        [
            EligibilityEvaluator.RulesFingerprint(new EligibilitySettings { Rules = [Rule(["h264", "hevc"], "1080p-1440p", "15Mbps", "800MiB")] }),
            EligibilityEvaluator.RulesFingerprint(new EligibilitySettings { Rules = [Rule(["h264"], "1440p-4k", "15Mbps", "800MiB")] }),
            EligibilityEvaluator.RulesFingerprint(new EligibilitySettings { Rules = [Rule(["h264"], "1080p-1440p", "10Mbps", "800MiB")] }),
            EligibilityEvaluator.RulesFingerprint(new EligibilitySettings { Rules = [Rule(["h264"], "1080p-1440p", "15Mbps", "500MiB")] }),
            EligibilityEvaluator.RulesFingerprint(new EligibilitySettings { Rules = [Rule(["h264"], "1080p-1440p", "15Mbps", "800MiB"), Rule(["hevc"], "1080p-1440p", "15Mbps", "800MiB")] })
        ];

        changed.Should().OnlyHaveUniqueItems().And.NotContain(baseline);
    }

    [Fact]
    public void DescribeMismatchNamesTheCodecWhenNoRuleWantsIt()
    {
        var settings = new EligibilitySettings { Rules = [Rule(["hevc"], "1080p-1440p", "10Mbps", "5B"), Rule(["vp9", "av1"], "4k+", "10Mbps", "5B")] };

        EligibilityEvaluator.DescribeMismatch(1_000, Described("h264", 1920, 1080, 20_000_000), settings).Should().Be("no rule for h264");
    }

    [Fact]
    public void DescribeMismatchListsWhatTheOnlyRuleNeedsAsWrittenInTheConfigWithoutANumber()
    {
        var settings = new EligibilitySettings { Rules = [Rule(["hevc"], "1080p-1440p", "10Mbps", "500MiB")] };

        EligibilityEvaluator.DescribeMismatch(100L * 1024 * 1024, Described("hevc", 1280, 720, 6_500_000), settings)
            .Should().Be("needs 500MiB and 10Mbps and 1080p-1440p");
    }

    [Fact]
    public void DescribeMismatchDescribesTheClosestRuleAndNumbersItWhenThereAreSeveral()
    {
        // Rule 1 fails on resolution and bitrate, rule 2 only on bitrate: rule 2 is the one worth telling the user about.
        var settings = new EligibilitySettings { Rules = [Rule(["hevc"], "4k+", "20Mbps", "1B"), Rule(["hevc"], "1080p-1440p", "10Mbps", "1B"), Rule(["h264"], "1080p-1440p", "1Mbps", "1B")] };

        EligibilityEvaluator.DescribeMismatch(1_000, Described("hevc", 1920, 1080, 8_000_000), settings).Should().Be("rule 2 needs 10Mbps");
    }

    [Fact]
    public void DescribeMismatchSaysWhenFfprobeReportedNoBitrate()
    {
        var settings = new EligibilitySettings { Rules = [Rule(["hevc"], "1080p-1440p", "10Mbps", "1B")] };

        EligibilityEvaluator.DescribeMismatch(1_000, Described("hevc", 1920, 1080, null), settings).Should().Be("needs a known bitrate");
    }

    [Fact]
    public void DescribeMismatchGivesTheSameTextForAlikeSkipsWhateverTheFilesValuesAre()
    {
        // The text doubles as the key that counts alike skips together, so it must not carry the file's own values.
        var settings = new EligibilitySettings { Rules = [Rule(["h264"], "1080p-1440p", "1Mbps", "2GiB"), Rule(["hevc"], "1080p-1440p", "1Mbps", "300MiB")] };

        var small = EligibilityEvaluator.DescribeMismatch(100L << 20, Described("h264", 1920, 1080, 40_000_000), settings);
        var smaller = EligibilityEvaluator.DescribeMismatch(200L << 20, Described("h264", 1920, 1080, 9_000_000), settings);

        small.Should().Be("rule 1 needs 2GiB");
        smaller.Should().Be(small);
    }

    [Fact]
    public void MatchingRuleIsTheFirstRuleInConfigOrderThatTheFileFits()
    {
        // The user's case: the same size fails the h264 rule and fits the hevc one.
        var settings = new EligibilitySettings { Rules = [Rule(["h264"], "4k+", "10Mbps", "2GiB"), Rule(["h264", "hevc"], "4k+", "10Mbps", "300MiB"), Rule(["hevc"], "4k+", "10Mbps", "1B")] };
        var size = 319L << 20;

        EligibilityEvaluator.MatchingRule(size, Described("hevc", 3840, 2160, 33_000_000), settings).Should().Be(2);
        EligibilityEvaluator.MatchingRule(size, Described("h264", 3840, 2160, 33_000_000), settings).Should().Be(2);
        EligibilityEvaluator.MatchingRule(100, Described("hevc", 3840, 2160, 33_000_000), settings).Should().Be(3);
        EligibilityEvaluator.MatchingRule(100, Described("h264", 3840, 2160, 33_000_000), settings).Should().BeNull();
    }

    private static MediaInfo Described(string codec, int width, int height, long? bitrate) => new(codec, 1, 1, 0, 0, 60, null, width, height, bitrate);

    private static EligibilityRuleSettings Rule(List<string> codecs, string resolution, string bitrate, string size) =>
        new() { Codecs = codecs, Resolution = resolution, MinimumVideoBitrate = bitrate, MinimumFileSize = size };

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
