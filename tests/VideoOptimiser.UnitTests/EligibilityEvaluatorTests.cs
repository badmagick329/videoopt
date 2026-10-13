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

        var mismatch = EligibilityEvaluator.DescribeMismatch(1_000, Described("h264", 1920, 1080, 20_000_000), settings);

        mismatch.Text.Should().Be("codec h264 (rules want hevc/vp9/av1)");
        mismatch.Key.Should().Be("codec h264");
    }

    [Fact]
    public void DescribeMismatchListsEveryFailureOfTheOnlyRuleWithTheRealValues()
    {
        var settings = new EligibilitySettings { Rules = [Rule(["hevc"], "1080p-1440p", "10Mbps", "500MiB")] };

        var mismatch = EligibilityEvaluator.DescribeMismatch(100L * 1024 * 1024, Described("hevc", 1280, 720, 6_500_000), settings);

        mismatch.Text.Should().Be("bitrate 6.5Mbps below 10Mbps; resolution 1280x720 outside 1080p-1440p; size 100.0 MiB below 500MiB");
        mismatch.Key.Should().Be("bitrate below 10Mbps; resolution outside 1080p-1440p; size below 500MiB");
    }

    [Fact]
    public void DescribeMismatchDescribesTheClosestRuleAndNumbersItWhenThereAreSeveral()
    {
        // Rule 1 fails on resolution and bitrate, rule 2 only on bitrate: rule 2 is the one worth telling the user about.
        var settings = new EligibilitySettings { Rules = [Rule(["hevc"], "4k+", "20Mbps", "1B"), Rule(["hevc"], "1080p-1440p", "10Mbps", "1B"), Rule(["h264"], "1080p-1440p", "1Mbps", "1B")] };

        var mismatch = EligibilityEvaluator.DescribeMismatch(1_000, Described("hevc", 1920, 1080, 8_000_000), settings);

        mismatch.Text.Should().Be("bitrate 8Mbps below 10Mbps (rule 2)");
        mismatch.Key.Should().Be("bitrate below 10Mbps (rule 2)");
    }

    [Fact]
    public void DescribeMismatchSaysWhenFfprobeReportedNoBitrate()
    {
        var settings = new EligibilitySettings { Rules = [Rule(["hevc"], "1080p-1440p", "10Mbps", "1B")] };

        var mismatch = EligibilityEvaluator.DescribeMismatch(1_000, Described("hevc", 1920, 1080, null), settings);

        mismatch.Text.Should().Be("bitrate unknown (needs 10Mbps)");
        mismatch.Key.Should().Be("bitrate unknown");
    }

    [Fact]
    public void DescribeMismatchAgreesWithIsEligibleAboutWhichFilesFail()
    {
        var rule = Rule(["h264"], "1080p-1440p", "8Mbps", "1B");
        foreach (var media in new[] { Described("hevc", 1920, 1080, 9_000_000), Described("h264", 1280, 720, 9_000_000), Described("h264", 1920, 1080, 7_999_999) })
        {
            IsEligible(rule, media).Should().BeFalse();
            EligibilityEvaluator.DescribeMismatch(10, media, new EligibilitySettings { Rules = [rule] }).Text.Should().NotBe("did not match");
        }
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
