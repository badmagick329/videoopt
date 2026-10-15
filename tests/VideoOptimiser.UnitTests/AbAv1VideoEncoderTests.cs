using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class AbAv1VideoEncoderTests
{
    private static readonly QualitySettings Settings = new()
    {
        Encoder = "libsvtav1",
        PixelFormat = "yuv420p10le",
        Preset = 6,
        MinimumVmaf = 95,
        CrfSearch = new CrfSearchSettings { MinCrf = 18, MaxCrf = 50, SampleCount = 5, SampleDuration = "20s" }
    };

    [Fact]
    public void BuildArgumentsExcludesDataStreamsAndKeepsSourceMetadata()
    {
        var arguments = new AbAv1VideoEncoder("ab-av1").BuildArguments("C:\\video.mp4", "C:\\out.mp4", 30, Settings, []);

        arguments.Should().Equal(
            "encode", "--input", "C:\\video.mp4", "--output", "C:\\out.mp4",
            "--encoder", "libsvtav1", "--pix-format", "yuv420p10le", "--preset", "6",
            "--crf", "30", "--enc", "map=-0:d",
            "--enc", "map_metadata=0", "--enc", "movflags=+faststart+use_metadata_tags");
    }

    [Fact]
    public void BuildArgumentsReEncodesEachPcmAudioStreamToFlacAndCopiesTheRest()
    {
        var arguments = new AbAv1VideoEncoder("ab-av1").BuildArguments("C:\\video.mov", "C:\\out.mp4", 30, Settings, [1, 3]);

        arguments.Should().Equal(
            "encode", "--input", "C:\\video.mov", "--output", "C:\\out.mp4",
            "--encoder", "libsvtav1", "--pix-format", "yuv420p10le", "--preset", "6",
            "--crf", "30", "--enc", "map=-0:d",
            "--enc", "map_metadata=0", "--enc", "movflags=+faststart+use_metadata_tags",
            "--enc", "c:a:1=flac", "--enc", "c:a:3=flac");
    }
}
