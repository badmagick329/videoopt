using FluentAssertions;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class FfprobeJsonParserTests
{
    [Fact]
    public void ParsePrefersSubstantiveDefaultVideoOverAttachedCoverArt()
    {
        const string json = """
            {
              "format": { "duration": "120.5", "size": "1000" },
              "streams": [
                { "codec_type": "video", "codec_name": "mjpeg", "disposition": { "default": 1, "attached_pic": 1 } },
                { "codec_type": "video", "codec_name": "h264", "width": 3840, "height": 2160, "bit_rate": 25000000, "disposition": { "default": 0, "attached_pic": 0 } },
                { "codec_type": "audio", "codec_name": "aac" },
                { "codec_type": "subtitle", "codec_name": "subrip" },
                { "codec_type": "attachment", "codec_name": "ttf" }
              ]
            }
            """;

        var media = FfprobeJsonParser.Parse(json);

        media.PrimaryVideoCodec.Should().Be("h264");
        media.VideoStreamCount.Should().Be(1);
        media.AudioStreamCount.Should().Be(1);
        media.SubtitleStreamCount.Should().Be(1);
        media.AttachmentCount.Should().Be(1);
        media.DurationSeconds.Should().Be(120.5);
        media.PrimaryVideoWidth.Should().Be(3840);
        media.PrimaryVideoHeight.Should().Be(2160);
        media.PrimaryVideoBitrate.Should().Be(25_000_000);
    }

    [Theory]
    [InlineData("""{ "duration": "100", "size": "1000000", "bit_rate": "61779628" }""", 61_779_628L)]
    [InlineData("""{ "duration": "100", "size": "1000000" }""", 80_000L)]
    [InlineData("""{ "size": "1000000" }""", null)]
    public void ParseFallsBackToTheWholeFileBitrateWhenTheVideoStreamHasNone(string format, long? expected)
    {
        var json = $$"""
            {
              "format": {{format}},
              "streams": [ { "codec_type": "video", "codec_name": "h264", "tags": { "DURATION": "00:01:40.000000000" } } ]
            }
            """;

        FfprobeJsonParser.Parse(json).PrimaryVideoBitrate.Should().Be(expected);
    }

    /// <summary>
    /// The APAC streams are built from an FFmpeg 8.1.1 encode log and FFmpeg 9.0's codec list, not captured (VOPT-125):
    /// 8.x gives spatial audio no codec name, 9.x names it apple_apac, and both keep the container's apac tag.
    /// </summary>
    [Fact]
    public void ParseListsTheAudioTracksInOrderWithTheirTags()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "hevc" },
                { "codec_type": "audio", "codec_name": "aac", "codec_tag_string": "mp4a" },
                { "codec_type": "subtitle", "codec_name": "subrip" },
                { "codec_type": "audio", "codec_tag_string": "apac", "codec_tag": "0x63617061", "sample_rate": "48000", "channels": 4 },
                { "codec_type": "audio", "codec_name": "apple_apac", "codec_tag_string": "apac", "codec_tag": "0x63617061", "sample_rate": "48000", "channels": 4 },
                { "codec_type": "audio", "codec_name": "pcm_s24le" }
              ]
            }
            """;

        FfprobeJsonParser.Parse(json).AudioTracks.Should().Equal(
            new AudioTrack("aac", "mp4a"), new AudioTrack("unknown", "apac"), new AudioTrack("apple_apac", "apac"), new AudioTrack("pcm_s24le"));
    }

    [Fact]
    public void ParseRejectsFilesWithOnlyAttachedCoverArt()
    {
        const string json = """
            { "streams": [
              { "codec_type": "video", "codec_name": "mjpeg", "disposition": { "attached_pic": 1 } }
            ] }
            """;

        var action = () => FfprobeJsonParser.Parse(json);

        action.Should().Throw<MediaProbeException>();
    }
}
