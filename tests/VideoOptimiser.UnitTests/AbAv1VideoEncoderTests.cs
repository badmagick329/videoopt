using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class AbAv1VideoEncoderTests
{
    [Fact]
    public void BuildArgumentsExcludesDataStreams()
    {
        var settings = new QualitySettings
        {
            Encoder = "libsvtav1",
            PixelFormat = "yuv420p10le",
            Preset = 6,
            MinimumVmaf = 95,
            CrfSearch = new CrfSearchSettings { MinCrf = 18, MaxCrf = 50, SampleCount = 5, SampleDuration = "20s" }
        };

        var arguments = new AbAv1VideoEncoder("ab-av1").BuildArguments("C:\\video.mp4", "C:\\out.mp4", 30, settings);

        arguments.Should().Equal(
            "encode", "--input", "C:\\video.mp4", "--output", "C:\\out.mp4",
            "--encoder", "libsvtav1", "--pix-format", "yuv420p10le", "--preset", "6",
            "--crf", "30", "--enc", "map=-0:d");
    }
}
