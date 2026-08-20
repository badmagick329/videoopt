using FluentAssertions;
using VideoOptimiser.Infrastructure.Scanning;

namespace VideoOptimiser.UnitTests;

public sealed class FfprobeMediaTimelineProbeTests
{
    [Fact]
    public void BuildPacketTimelineArgumentsKeepsShowPacketsBeforeEntrySelection()
    {
        var arguments = FfprobeMediaTimelineProbe.BuildPacketTimelineArguments("C:\\video.mkv");

        arguments.Should().Equal(
            "-v", "error",
            "-show_packets",
            "-show_entries", "packet=stream_index,pts_time,duration_time",
            "-of", "compact=p=0:nk=1",
            "C:\\video.mkv");
    }

    [Fact]
    public void BuildStreamSelectionArgumentsKeepsShowStreamsBeforeEntrySelection()
    {
        var arguments = FfprobeMediaTimelineProbe.BuildStreamSelectionArguments("C:\\video.mkv");

        arguments.Should().Equal(
            "-v", "error",
            "-show_streams",
            "-show_entries", "stream=index,codec_type,disposition",
            "-print_format", "json",
            "C:\\video.mkv");
    }

    [Fact]
    public void ParseCompactPacketLinesBuildsTimelineFromFfprobeEightRows()
    {
        var timeline = FfprobeMediaTimelineProbe.ParseCompactPacketLines(
            ["0|1.881000|0.041000", "0|1.964000|0.041000", "1|4.132000|0.021333"],
            primaryVideoStreamIndex: 0,
            primaryAudioStreamIndex: 1);

        timeline.FirstVideoTimestampSeconds.Should().Be(1.881);
        timeline.LastVideoEndTimestampSeconds.Should().Be(2.005);
        timeline.PrimaryVideoPacketCount.Should().Be(2);
        timeline.FirstPrimaryAudioTimestampSeconds.Should().Be(4.132);
    }
}
