using FluentAssertions;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class FfmpegInterlaceDetectorTests
{
    /// <summary>FFmpeg 9.0.2 configures the filter graph twice for some inputs, and the first graph reports all zeros.</summary>
    [Fact]
    public async Task DetectReadsTheLastSingleFrameReport()
    {
        const string stderr = """
            [Parsed_idet_0 @ 000002486407e480] Repeated Fields: Neither:     0 Top:     0 Bottom:     0
            [Parsed_idet_0 @ 000002486407e480] Single frame detection: TFF:     0 BFF:     0 Progressive:     0 Undetermined:     0
            [Parsed_idet_0 @ 000002486407e480] Multi frame detection: TFF:     0 BFF:     0 Progressive:     0 Undetermined:     0
            [Parsed_idet_0 @ 000002486407df80] Repeated Fields: Neither:   500 Top:     0 Bottom:     0
            [Parsed_idet_0 @ 000002486407df80] Single frame detection: TFF:   410 BFF:   302 Progressive:  5050 Undetermined:     2
            [Parsed_idet_0 @ 000002486407df80] Multi frame detection: TFF:   337 BFF:   238 Progressive:  5189 Undetermined:     0
            """;
        var runner = new ScriptedProcessRunner([new ExternalProcessResult(0, string.Empty, stderr)]);

        var fields = await new FfmpegInterlaceDetector("ffmpeg", runner).DetectAsync("C:\\video.ts", 192.7);

        fields.Should().Be(new FieldCounts(410, 302, 5050, 2));
        runner.Requests.Single().Arguments.Should().Equal(
            "-hide_banner", "-nostdin", "-nostats", "-ss", "30", "-i", "C:\\video.ts", "-map", "0:V:0", "-vf", "idet",
            "-frames:v", "500", "-an", "-sn", "-dn", "-f", "null", "-");
    }

    // A video no longer than 90 s is read from its start: past a 30 s opening there would be too little left.
    [Theory]
    [InlineData(90.0)]
    [InlineData(null)]
    public void AShortOrUnmeasuredVideoIsReadFromItsStart(double? duration) =>
        FfmpegInterlaceDetector.BuildArguments("C:\\video.ts", duration).Should().NotContain("-ss");
}
