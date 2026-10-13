using FluentAssertions;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class AbAv1OutputTranslatorTests
{
    private TimeSpan _now;

    [Fact]
    public void TheRealRemoteCrfSearchBecomesOneLinePerAttemptWithItsVerdict()
    {
        // remote-crf-search.log keeps attempt 34's lines whole and only the verdict lines of the later attempts.
        Translate(AbAv1Stage.CrfSearch, "remote-crf-search.log", minimumVmaf: 97, maxEncodedPercent: 80).Should().Equal(
            JobProgress.Detail("CRF 34   VMAF 95.02   size  19%   below target"),
            JobProgress.Detail("CRF 18   VMAF 99.46   size  61%   passes"),
            JobProgress.Detail("CRF 27   VMAF 97.72   size  33%   passes"),
            JobProgress.Detail("CRF 29   VMAF 97.13   size  29%   passes, chosen"));
    }

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
    public void TheRealLocalCrfSearchReadsCachedAttemptsAndTheChosenOneFromBothStreams()
    {
        var lines = Translate(AbAv1Stage.CrfSearch, "local-crf-search.stderr.log", minimumVmaf: 93, maxEncodedPercent: 90)
            .Concat(Translate(AbAv1Stage.CrfSearch, "local-crf-search.stdout.log", minimumVmaf: 93, maxEncodedPercent: 90));

        lines.Should().Equal(
            JobProgress.Detail("CRF 34   VMAF 92.55   size  46%   below target"),
            JobProgress.Detail("CRF 18   VMAF 97.45   size 137%   passes, file too big"),
            JobProgress.Detail("CRF 33   VMAF 92.81   size  50%   below target"),
            JobProgress.Detail("CRF 32   VMAF 93.44   size  57%   passes, chosen"));
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
            JobProgress.Detail("20%, 54 fps, about 1m 06s left"),
            JobProgress.Detail("42%, 57 fps, about 45s left"),
            JobProgress.Step("Encoded 99.81 MiB (29% of the original) in 1m 00s."));
    }

    [Fact]
    public void TheRealLocalEncodeReportsItsEncodedSize()
    {
        _now = TimeSpan.FromSeconds(14);

        Translate(AbAv1Stage.Encode, "local-encode.stderr.log").Should().Equal(JobProgress.Step("Encoded 49.17 MiB (75% of the original) in 14s."));
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
    public void WrappedProgressTranslatesToolOutputAndPassesEverythingElseUnchanged()
    {
        var captured = new List<JobProgress>();
        var progress = Create(AbAv1Stage.CrfSearch, minimumVmaf: 97).Wrap(new Capture(captured));

        progress.Report(JobProgress.Step("Finding the best CRF..."));
        progress.Report(JobProgress.Tool("[2026-10-13T10:10:36Z INFO  ab_av1::command::crf_search] crf 34 VMAF 95.02 (19%)"));
        progress.Report(JobProgress.Tool("[2026-10-13T10:09:19Z INFO  ab_av1::vmaf] vmaf source.sample51+600f.av1.crf34.7.mp4 vs reference source.sample51+600f.mkv"));

        captured.Should().Equal(JobProgress.Step("Finding the best CRF..."), JobProgress.Detail("CRF 34   VMAF 95.02   size  19%   below target"));
    }

    [Theory]
    [InlineData("66 seconds", 66)]
    [InlineData("1 second", 1)]
    [InlineData("3 minutes", 180)]
    [InlineData("1 hour", 3600)]
    public void ParsesAbAv1Etas(string eta, int seconds) => AbAv1OutputTranslator.ParseEta(eta).Should().Be(TimeSpan.FromSeconds(seconds));

    private List<JobProgress> Translate(AbAv1Stage stage, string fixture, double minimumVmaf = 95, int maxEncodedPercent = 80)
    {
        var translator = Create(stage, minimumVmaf, maxEncodedPercent);
        return Lines(fixture).SelectMany(translator.Translate).ToList();
    }

    private AbAv1OutputTranslator Create(AbAv1Stage stage, double minimumVmaf = 95, int maxEncodedPercent = 80) => new(stage, minimumVmaf, maxEncodedPercent, () => _now);

    private static string[] Lines(string fixture) => AbAv1OutputTests.Fixture(fixture).Split(["\r\n", "\n"], StringSplitOptions.None);

    private sealed class Capture(List<JobProgress> lines) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => lines.Add(value);
    }
}
