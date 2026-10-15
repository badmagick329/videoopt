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

    /// <summary>Data streams are excluded and the source's metadata kept; each PCM audio stream is re-encoded to FLAC and the rest copied.</summary>
    [Theory]
    [InlineData(new int[0], new string[0])]
    [InlineData(new[] { 1, 3 }, new[] { "--enc", "c:a:1=flac", "--enc", "c:a:3=flac" })]
    public void BuildArgumentsExcludesDataStreamsKeepsSourceMetadataAndReEncodesPcmAudioToFlac(int[] pcmStreams, string[] flacArguments)
    {
        var arguments = new AbAv1VideoEncoder("ab-av1").BuildArguments("C:\\video.mp4", "C:\\out.mp4", 30, Settings, pcmStreams);

        string[] expected =
        [
            "encode", "--input", "C:\\video.mp4", "--output", "C:\\out.mp4",
            "--encoder", "libsvtav1", "--pix-format", "yuv420p10le", "--preset", "6",
            "--crf", "30", "--enc", "map=-0:d",
            "--enc", "map_metadata=0", "--enc", "movflags=+faststart+use_metadata_tags",
            .. flacArguments
        ];
        arguments.Should().Equal(expected);
    }
}
