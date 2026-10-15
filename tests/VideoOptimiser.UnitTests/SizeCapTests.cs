using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class SizeCapTests
{
    private const long MiB = 1024 * 1024;

    // Bitrate × duration ÷ 8 is the video stream: 1024 s keeps it a whole number of MiB and leaves ab-av1 cutting samples,
    // which drop the audio, so the stream is measured. At 32 s the samples would cover over 85% of the video, so ab-av1
    // encodes it whole and measures the file, audio included. Every row uses savings.minimumBytesSaved 100MiB and
    // quality.maxEncodedPercent 80; the stream is the file less 20 MiB of audio unless the row says otherwise.
    [Theory]
    [InlineData(1024, 1024d, 1044, 80, false, false, 1024)] // maxEncodedPercent is lower
    [InlineData(300, 1024d, 320, 66, true, false, 300)]      // the minimum saving is lower: floor(200 / 300)
    [InlineData(500, 1024d, 520, 80, false, false, 500)]     // a tie goes to maxEncodedPercent
    [InlineData(100, 1024d, 120, 0, true, false, 100)]       // nothing left to save: rules out every CRF
    [InlineData(300, 32d, 400, 75, true, true, 400)]         // whole-video test: floor(300 / 400), the file both times
    [InlineData(null, 1024d, 300, 66, true, true, 300)]      // unknown bitrate: the file stands in for the stream
    public void TheCapIsTheLowerOfMaxEncodedPercentAndTheShareThatStillSavesTheMinimum(int? streamMiB, double durationSeconds, int fileMiB, int percent, bool setByMinimumBytes, bool measuredIsFile, int measuredMiB)
    {
        long? bitrate = streamMiB is { } stream ? (long)(stream * MiB * 8 / durationSeconds) : null;
        var media = new MediaInfo("h264", 1, 1, 0, 0, durationSeconds, fileMiB * MiB, 1920, 1080, bitrate);

        var cap = SizeCap.For(media, fileMiB * MiB, new QualitySettings { MaxEncodedPercent = 80 }, new SavingsSettings { MinimumBytesSaved = "100MiB" });

        cap.Percent.Should().Be(percent);
        cap.SetByMinimumBytes.Should().Be(setByMinimumBytes);
        cap.MeasuredIsFile.Should().Be(measuredIsFile);
        cap.MeasuredBytes.Should().Be(measuredMiB * MiB);
        cap.RulesOutEverything.Should().Be(percent <= 0);
    }

    // A 300 MiB stream, cap 66%. 79.2% of it is 237.6 MiB, so the passing attempt saves 62.4 MiB. Reaching the lowest CRF
    // below the VMAF is a quality verdict, so it keeps its own reason whatever set the cap.
    [Theory]
    [InlineData(30, 97.2, 79.2, "AV1 would save about 62.4 MiB at the best quality-passing setting (CRF 30, VMAF 97.2); the minimum is 100MiB (savings.minimumBytesSaved).")]
    [InlineData(30, 96.1, 25d, "No CRF reached VMAF 97 while saving 100MiB (savings.minimumBytesSaved).")]
    [InlineData(18, 96.3, 70d, "No CRF reached VMAF 97; even CRF 18, the lowest allowed (quality.crfSearch.minCrf), scored 96.3.")]
    public void WhenTheMinimumSavingSetTheCapTheSkipReasonNamesItInBytes(int crf, double vmaf, double encodedPercent, string reason)
    {
        var cap = new SizeCap(66, true, 300 * MiB, false, 100 * MiB, "100MiB");
        var quality = new QualitySettings { MinimumVmaf = 97 }.WithSizeCap(cap);

        new NoWorthwhileCrfException([new CrfAttempt(crf, vmaf, encodedPercent)], quality).Message.Should().Be(reason);
    }
}
