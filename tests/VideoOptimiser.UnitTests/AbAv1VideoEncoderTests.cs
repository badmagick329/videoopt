using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Tools;
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

    /// <summary>
    /// Data streams are excluded, every frame keeps its timestamp, and the source's metadata is kept; each PCM audio stream is
    /// re-encoded to FLAC and the rest copied.
    /// </summary>
    [Theory]
    [InlineData(new[] { "aac" }, new string[0], new string[0])]
    [InlineData(new[] { "aac", "pcm_s16le", "pcm_s24le" }, new string[0], new[] { "--enc", "c:a:1=flac", "--enc", "c:a:2=flac" })]
    // The apac stream is input 1; the PCM after it is input 2 but output 1, so the map names the input and the codec the output.
    [InlineData(new[] { "aac", "unknown", "pcm_s24le" }, new[] { "--enc", "map=-0:a:1" }, new[] { "--enc", "c:a:1=flac" })]
    public void BuildArgumentsExcludesDataStreamsKeepsSourceMetadataDropsUnsupportedAudioAndReEncodesPcmAudioToFlac(string[] codecs, string[] dropArguments, string[] flacArguments)
    {
        var plan = AudioPlan.For(new MediaInfo("h264", 1, codecs.Length, 0, 0, 1, null, 1920, 1080, null, [.. codecs.Select(codec => new AudioTrack(codec))]));

        var arguments = new AbAv1VideoEncoder("ab-av1").BuildArguments("C:\\video.mp4", "C:\\out.mp4", 30, Settings, plan);

        string[] expected =
        [
            "encode", "--input", "C:\\video.mp4", "--output", "C:\\out.mp4",
            "--encoder", "libsvtav1", "--pix-format", "yuv420p10le", "--preset", "6",
            "--crf", "30", "--enc", "map=-0:d", .. dropArguments, "--enc", "fps_mode=passthrough",
            "--enc", "map_metadata=0", "--enc", "movflags=+faststart+use_metadata_tags",
            .. flacArguments
        ];
        arguments.Should().Equal(expected);
    }
}
