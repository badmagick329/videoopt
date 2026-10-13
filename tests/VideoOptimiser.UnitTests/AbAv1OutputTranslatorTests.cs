using FluentAssertions;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class AbAv1OutputTranslatorTests
{
    private TimeSpan _now;

    [Fact]
    public void TheRealRemoteNotWorthConvertingSearchMarksTheAttemptsThatPassedButWereTooBig()
    {
        // The error line is left to the job's outcome, which says why the video is not worth converting.
        Translate(AbAv1Stage.CrfSearch, "remote-crf-not-worth-converting.log", minimumVmaf: 97, maxEncodedPercent: 80).Should().Equal(
            JobProgress.Detail("CRF 34   VMAF 95.35   size  71%   below target"),
            JobProgress.Detail("CRF 18   VMAF 99.75   size 239%   passes, file too big"),
            JobProgress.Detail("CRF 28   VMAF 98.47   size 122%   passes, file too big"),
            JobProgress.Detail("CRF 31   VMAF 97.31   size  95%   passes, file too big"),
            JobProgress.Detail("CRF 32   VMAF 96.64   size  86%   below target"));
    }

    [Fact]
    public void TheRealRemoteEncodeShowsThrottledProgressThenTheEncodedSizeAndDropsTheTimeReport()
    {
        var translator = Create(AbAv1Stage.Encode);
        var output = new List<JobProgress>();
        // The lines arrive 16 s apart, except the last progress line, which follows the one before within the interval.
        foreach (var (line, at) in Lines("remote-encode.log").Zip([0, 16, 32, 36, 60, 60, 60, 60, 60, 60]))
        {
            _now = TimeSpan.FromSeconds(at);
            output.AddRange(translator.Translate(line));
        }

        output.Should().Equal(
            JobProgress.Detail("20%, 54 fps"),
            JobProgress.Detail("42%, 57 fps"),
            JobProgress.Step("Encoded 99.8 MiB (29% of the original) in 1m 00s."));
    }

    [Fact]
    public void AnUnrecognisedLinePassesThroughSoNothingImportantIsHidden()
    {
        var translator = Create(AbAv1Stage.Encode);

        translator.Translate("Error: ffmpeg encode exit code 1").Should().Equal(JobProgress.Detail("Error: ffmpeg encode exit code 1"));
        translator.Translate("[2026-10-13T10:06:43Z WARN  ab_av1::command::encode] input has no audio").Should().Equal(JobProgress.Detail("ab-av1 warn: input has no audio"));
        translator.Translate("[2026-10-13T10:06:43Z INFO  ab_av1::some_future_module] something new").Should().Equal(JobProgress.Detail("something new"));
    }

    [Fact]
    public void AFailedRemoteStagesExitLineIsDropped() =>
        Create(AbAv1Stage.CrfSearch).Translate("Command exited with non-zero status 1").Should().BeEmpty();

    [Fact]
    public void TheTimeLeftIsMeasuredFromTheProgressSeenNotTakenFromAbAv1sCoarseEstimate()
    {
        var translator = Create(AbAv1Stage.Encode);

        // ab-av1 said "2 minutes" on every one of these lines; the pace between the first line and each later one decides.
        Progress(translator, 0, 10).Should().Equal(JobProgress.Detail("10%, 8.5 fps"));
        Progress(translator, 10, 15).Should().Equal(JobProgress.Detail("15%, 8.5 fps"));
        Progress(translator, 20, 20).Should().Equal(JobProgress.Detail("20%, 8.5 fps"));
        // 20 points in 40 s: 70 points left take 140 s.
        Progress(translator, 40, 30).Should().Equal(JobProgress.Detail("30%, 8.5 fps, about 2m 20s left"));
        // 80 points in 100 s: 10 points left take 12.5 s.
        Progress(translator, 100, 90).Should().Equal(JobProgress.Detail("90%, 8.5 fps, about 12s left"));
    }

    [Fact]
    public void NoTimeLeftIsShownUntilBothEnoughProgressAndEnoughTimeHavePassed()
    {
        var translator = Create(AbAv1Stage.Encode);

        Progress(translator, 0, 10);
        // 30 s but only 4 points: the pace is still noise.
        Progress(translator, 30, 14).Should().Equal(JobProgress.Detail("14%, 8.5 fps"));
        // 5 points but 20 s: the same.
        var quick = Create(AbAv1Stage.Encode);
        Progress(quick, 0, 10);
        Progress(quick, 20, 15).Should().Equal(JobProgress.Detail("15%, 8.5 fps"));
        // Both reached.
        Progress(translator, 60, 15).Should().Equal(JobProgress.Detail("15%, 8.5 fps, about 17m 00s left"));
    }

    [Fact]
    public void ThePaceStartsAtTheFirstProgressLine()
    {
        var translator = Create(AbAv1Stage.Encode);

        Progress(translator, 0, 10);
        Progress(translator, 3, 11).Should().BeEmpty();
        // 20 points in 50 s: 70 points left take 175 s.
        Progress(translator, 50, 30).Should().Equal(JobProgress.Detail("30%, 8.5 fps, about 2m 55s left"));
    }

    private IReadOnlyList<JobProgress> Progress(AbAv1OutputTranslator translator, int seconds, int percent)
    {
        _now = TimeSpan.FromSeconds(seconds);
        return translator.Translate($"[2026-10-13T10:06:59Z INFO  ab_av1::command::encode] {percent}%, 8.5 fps, eta 2 minutes");
    }

    private List<JobProgress> Translate(AbAv1Stage stage, string fixture, double minimumVmaf = 95, int maxEncodedPercent = 80)
    {
        var translator = Create(stage, minimumVmaf, maxEncodedPercent);
        return Lines(fixture).SelectMany(translator.Translate).ToList();
    }

    private AbAv1OutputTranslator Create(AbAv1Stage stage, double minimumVmaf = 95, int maxEncodedPercent = 80) => new(stage, minimumVmaf, maxEncodedPercent, () => _now);

    private static string[] Lines(string fixture) => AbAv1OutputTests.Fixture(fixture).Split(["\r\n", "\n"], StringSplitOptions.None);
}
