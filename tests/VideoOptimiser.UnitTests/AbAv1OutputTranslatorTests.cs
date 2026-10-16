using FluentAssertions;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class AbAv1OutputTranslatorTests
{
    private TimeSpan _now;

    [Fact]
    public void TheRealRemoteNotWorthConvertingSearchMarksTheAttemptsThatPassedButWereTooBig()
    {
        // The error line is left to the job's outcome, which says why the video is not worth converting. The log's last line,
        // "Command exited with non-zero status 1", is the remote stage's own exit report and is dropped too.
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
            JobProgress.Reading("20%, 54 fps", 20),
            JobProgress.Reading("42%, 57 fps", 42),
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
    public void TheTimeLeftIsMeasuredFromTheProgressSeenNotTakenFromAbAv1sCoarseEstimate()
    {
        var translator = Create(AbAv1Stage.Encode);

        // ab-av1 said "2 minutes" on every one of these lines; the pace between the first line and each later one decides.
        Progress(translator, 0, 10).Should().Equal(JobProgress.Reading("10%, 8.5 fps", 10));
        Progress(translator, 10, 15).Should().Equal(JobProgress.Reading("15%, 8.5 fps", 15));
        Progress(translator, 20, 20).Should().Equal(JobProgress.Reading("20%, 8.5 fps", 20));
        // 20 points in 40 s: 70 points left take 140 s.
        Progress(translator, 40, 30).Should().Equal(JobProgress.Reading("30%, 8.5 fps, about 2m 20s left", 30, TimeSpan.FromSeconds(140)));
        // 80 points in 100 s: 10 points left take 12.5 s.
        Progress(translator, 100, 90).Should().Equal(JobProgress.Reading("90%, 8.5 fps, about 12s left", 90, TimeSpan.FromSeconds(12.5)));
    }

    [Fact]
    public void NoTimeLeftIsShownUntilBothEnoughProgressAndEnoughTimeHavePassed()
    {
        var translator = Create(AbAv1Stage.Encode);

        Progress(translator, 0, 10);
        // 30 s but only 4 points: the pace is still noise.
        Progress(translator, 30, 14).Should().Equal(JobProgress.Reading("14%, 8.5 fps", 14));
        // Both reached.
        Progress(translator, 60, 15).Should().Equal(JobProgress.Reading("15%, 8.5 fps, about 17m 00s left", 15, TimeSpan.FromMinutes(17)));
    }

    /// <summary>A stage that began before this command has run for longer than this translator has existed, so the time it measures is not the stage's.</summary>
    [Fact]
    public void AStageAlreadyRunningWhenTheCommandReachedItReportsNoDuration()
    {
        var translator = Create(AbAv1Stage.Encode);
        _now = TimeSpan.FromMinutes(3);

        translator.Translate("Encoded 99.8 MiB (29%)").Should().Equal(JobProgress.Step("Encoded 99.8 MiB (29% of the original) in 3m 00s."));
        translator.StageWasAlreadyRunning();
        translator.Translate("Encoded 99.8 MiB (29%)").Should().Equal(JobProgress.Step("Encoded 99.8 MiB (29% of the original)."));
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
