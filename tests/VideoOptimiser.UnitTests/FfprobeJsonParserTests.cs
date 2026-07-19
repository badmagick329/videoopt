using FluentAssertions;
using VideoOptimiser.Infrastructure.Scanning;

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
                { "codec_type": "video", "codec_name": "h264", "disposition": { "default": 0, "attached_pic": 0 } },
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

        action.Should().Throw<InvalidDataException>();
    }
}
