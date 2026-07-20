using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class AbAv1CrfSearchClientTests
{
    [Fact]
    public void BuildArgumentsUsesTheInstalledAbAv1CrfSearchFlags()
    {
        var settings = new QualitySettings
        {
            Encoder = "libsvtav1",
            PixelFormat = "yuv420p10le",
            Preset = 6,
            MinimumVmaf = 95,
            CrfSearch = new CrfSearchSettings { MinCrf = 18, MaxCrf = 50, SampleCount = 5, SampleDuration = "20s" }
        };

        var arguments = new AbAv1CrfSearchClient("ab-av1").BuildArguments("C:\\video.mp4", settings);

        arguments.Should().Equal(
            "crf-search", "--input", "C:\\video.mp4", "--encoder", "libsvtav1", "--pix-format", "yuv420p10le",
            "--preset", "6", "--min-vmaf", "95", "--min-crf", "18", "--max-crf", "50", "--samples", "5", "--sample-duration", "20s");
    }

    [Fact]
    public void ParseRejectsAmbiguousOutput()
    {
        var action = () => CrfSearchOutputParser.Parse("search completed");

        action.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ParseReadsSuccessfulCrfFromAbAv1Version093Output()
    {
        const string output = """
            [2026-07-19T04:03:40Z INFO  ab_av1::command::crf_search] crf 44 successful
            crf 44 VMAF 95.02 predicted video stream size 388.44 MiB (15%) taking 34 minutes
            """;

        CrfSearchOutputParser.Parse(output).Should().Be(44);
    }

    [Fact]
    public void ParseReadsSuccessfulCrfWhenCombinedOutputContainsOtherCrfResults()
    {
        const string output = "crf 45 VMAF 96.87\n";
        const string error = "[INFO ab_av1::command::crf_search] crf 44 successful";

        CrfSearchOutputParser.Parse(output + Environment.NewLine + error).Should().Be(44);
    }
}
