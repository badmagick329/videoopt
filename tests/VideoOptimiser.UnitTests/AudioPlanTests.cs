using FluentAssertions;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class AudioPlanTests
{
    [Theory]
    [InlineData("aac", "mp4a", null)]
    [InlineData("pcm_s16le", null, null)]
    [InlineData("unknown", "apac", "apac")]
    [InlineData("none", "apac", "apac")]
    [InlineData("apple_apac", "apac", "apac")]
    [InlineData("unknown", "[0][0][0][0]", "unknown")]
    public void DropsOnlyTheStreamsFfmpegCannotIdentify(string codec, string? tag, string? droppedLabel)
    {
        var plan = AudioPlan.For(Media(new AudioTrack("aac", "mp4a"), new AudioTrack(codec, tag)));

        plan.Dropped.Should().Equal(droppedLabel is null ? [] : [new DroppedAudioTrack(1, droppedLabel)]);
        plan.LeavesNoAudio.Should().BeFalse();
    }

    [Fact]
    public void CountsFlacIndexesAmongTheKeptStreams()
    {
        var plan = AudioPlan.For(Media(new AudioTrack("aac"), new AudioTrack("unknown", "apac"), new AudioTrack("pcm_s24le")));

        plan.Dropped.Should().Equal(new DroppedAudioTrack(1, "apac"));
        plan.FlacOutputStreams.Should().Equal(1);
        plan.KeptAudioStreamCount.Should().Be(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LeavesNoAudioOnlyWhenEveryStreamIsDropped(bool withAac)
    {
        AudioTrack[] streams = withAac ? [new("aac"), new("apple_apac", "apac")] : [new("apple_apac", "apac")];

        AudioPlan.For(Media(streams)).LeavesNoAudio.Should().Be(!withAac);
    }

    [Fact]
    public void ASilentOriginalLosesNoAudio() =>
        AudioPlan.For(Media()).LeavesNoAudio.Should().BeFalse();

    private static MediaInfo Media(params AudioTrack[] audio) =>
        new("hevc", 1, audio.Length, 0, 0, 10, 1000, AudioTracks: audio);
}
