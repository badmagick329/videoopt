using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class AbAv1CrfSearchClientTests
{
    [Fact]
    public void BuildArgumentsUsesTheInstalledAbAv1CrfSearchFlags()
    {
        var settings = new QualitySettings
        {
            Encoder = "libsvtav1",
            PixelFormat = "yuv420p10le",
            Preset = 6,
            MinimumVmaf = 95,
            CrfSearch = new CrfSearchSettings { MinCrf = 18, MaxCrf = 50, SampleCount = 5, SampleDuration = "20s" }
        };

        var arguments = new AbAv1CrfSearchClient("ab-av1").BuildArguments("C:\\video.mp4", settings);

        arguments.Should().Equal(
            "crf-search", "--input", "C:\\video.mp4", "--encoder", "libsvtav1", "--pix-format", "yuv420p10le",
            "--preset", "6", "--min-vmaf", "95", "--max-encoded-percent", "80", "--min-crf", "18", "--max-crf", "50", "--crf-increment", "1", "--samples", "5", "--sample-duration", "20s");
    }

    [Fact]
    public async Task SearchThrowsTheSkipExceptionWithTheSmallestQualityPassingAttemptWhenNoCrfIsSmallEnough()
    {
        const string stderr = """
            [2026-10-13T10:55:18Z INFO  ab_av1::command::crf_search] crf 28 VMAF 98.47 (122%)
            [2026-10-13T10:56:02Z INFO  ab_av1::command::sample_encode] crf 31 VMAF 97.31 predicted video stream size 418.66 MiB (95%) taking 2 minutes
            [2026-10-13T10:56:02Z INFO  ab_av1::command::crf_search] crf 31 VMAF 97.31 (95%)
            [2026-10-13T10:56:45Z INFO  ab_av1::command::crf_search] crf 32 VMAF 96.64 (86%)
            Error: Failed to find a suitable crf
            """;

        var thrown = (await FluentActions.Awaiting(() => SearchFailingWithAsync(stderr, new QualitySettings { MinimumVmaf = 97, MaxEncodedPercent = 80 })).Should().ThrowAsync<NoWorthwhileCrfException>()).Which;

        thrown.Best.Should().Be(new CrfAttempt(31, 97.31, 95));
        thrown.Message.Should().Be("AV1 would make it 95% of the original's size at the best quality-passing setting (CRF 31, VMAF 97.3); the limit is 80% (quality.maxEncodedPercent).");
    }

    [Fact]
    public async Task WhenEvenTheLowestAllowedCrfMissesTheVmafTheReasonNamesThatCrfNotTheSizeLimit()
    {
        const string stderr = """
            [2026-10-13T10:55:18Z INFO  ab_av1::command::crf_search] crf 34 VMAF 91.39 (10%)
            [2026-10-13T10:55:58Z INFO  ab_av1::command::crf_search] crf 18 VMAF 96.34 (46%)
            Error: Failed to find a suitable crf
            """;

        var thrown = (await FluentActions.Awaiting(() => SearchFailingWithAsync(stderr, new QualitySettings { MinimumVmaf = 97 })).Should().ThrowAsync<NoWorthwhileCrfException>()).Which;

        thrown.Best.Should().BeNull();
        thrown.Message.Should().Be("No CRF reached VMAF 97; even CRF 18, the lowest allowed (quality.crfSearch.minCrf), scored 96.3.");
    }

    /// <summary>ab-av1 tries no lower CRF once an attempt is over the size limit, so the lowest CRF's VMAF is unknown and both limits are named.</summary>
    [Fact]
    public async Task WhenTheSearchStoppedAboveTheLowestCrfTheReasonNamesBothLimits()
    {
        const string stderr = """
            [2026-10-13T10:55:18Z INFO  ab_av1::command::crf_search] crf 30 VMAF 96.10 (85%)
            Error: Failed to find a suitable crf
            """;

        var thrown = (await FluentActions.Awaiting(() => SearchFailingWithAsync(stderr, new QualitySettings { MinimumVmaf = 97 })).Should().ThrowAsync<NoWorthwhileCrfException>()).Which;

        thrown.Best.Should().BeNull();
        thrown.Message.Should().Be("No CRF reached VMAF 97 within 80% of the original's size (quality.maxEncodedPercent).");
    }

    [Fact]
    public async Task SearchReportsTheErrorLineNotTheFirstInfoLineForOtherFailures()
    {
        const string stderr = """
            [2026-10-13T10:55:18Z INFO  ab_av1::command::crf_search] encoding sample 1/5
            Error: ffmpeg encode: Invalid data found when processing input
            """;

        await FluentActions.Awaiting(() => SearchFailingWithAsync(stderr, new QualitySettings()))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("ab-av1 crf-search failed with exit code 1: ffmpeg encode: Invalid data found when processing input");
    }

    [Fact]
    public async Task SearchReportsTheLastLineWhenThereIsNoErrorLine()
    {
        await FluentActions.Awaiting(() => SearchFailingWithAsync("first line\nsecond line\n", new QualitySettings()))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*: second line");
    }

    // A script stands in for ab-av1: it writes the log to stderr as ab-av1 does and exits 1.
    private static async Task SearchFailingWithAsync(string stderr, QualitySettings settings)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.AbAv1.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "stderr.txt"), stderr);
            var script = Path.Combine(directory, "ab-av1.cmd");
            await File.WriteAllTextAsync(script, "@echo off\r\ntype \"%~dp0stderr.txt\" 1>&2\r\nexit /b 1\r\n");
            _ = await new AbAv1CrfSearchClient(script).SearchAsync("C:\\video.mp4", settings);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ParseRejectsAmbiguousOutput()
    {
        var action = () => CrfSearchOutputParser.Parse("search completed");

        action.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ParseReadsSuccessfulCrfWhenCombinedOutputContainsOtherCrfResults()
    {
        const string output = "crf 45 VMAF 96.87\n";
        const string error = "[INFO ab_av1::command::crf_search] crf 44 successful";

        CrfSearchOutputParser.Parse(output + Environment.NewLine + error).Should().Be(44);
    }

    [Theory]
    [InlineData("remote-crf-search.log", 29, "284.87 MiB", 29, "5 minutes")]
    // A rerun reuses earlier samples and ends such lines with " (cache)", which is not part of the time.
    [InlineData("local-crf-search.stderr.log", 34, "29.97 MiB", 46, "17 seconds")]
    public void TryParsePredictionReadsTheChosenCrfsEstimateFromARealLog(string fixture, int crf, string size, int percent, string taking)
    {
        CrfSearchOutputParser.TryParsePrediction(AbAv1OutputTests.Fixture(fixture), crf, out var prediction).Should().BeTrue();

        prediction.Should().Be(new CrfPrediction(size, percent, taking));
    }

    [Fact]
    public void TryParsePredictionFindsNothingForACrfThatWasNeverEstimated()
    {
        CrfSearchOutputParser.TryParsePrediction(AbAv1OutputTests.Fixture("remote-crf-search.log"), 30, out var prediction).Should().BeFalse();

        prediction.Should().BeNull();
    }

    [Fact]
    public async Task ASuccessfulSearchReportsEveryLineAsToolOutputAndCarriesThePrediction()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.AbAv1.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "stdout.txt"), AbAv1OutputTests.Fixture("local-crf-search.stdout.log"));
            await File.WriteAllTextAsync(Path.Combine(directory, "stderr.txt"), AbAv1OutputTests.Fixture("local-crf-search.stderr.log"));
            var script = Path.Combine(directory, "ab-av1.cmd");
            await File.WriteAllTextAsync(script, "@echo off\r\ntype \"%~dp0stdout.txt\"\r\ntype \"%~dp0stderr.txt\" 1>&2\r\nexit /b 0\r\n");
            var reported = new List<JobProgress>();

            var result = await new AbAv1CrfSearchClient(script).SearchAsync("C:\\video.mp4", new QualitySettings(), new ListProgress(reported));

            result.Crf.Should().Be(32);
            result.Prediction.Should().Be(new CrfPrediction("37.07 MiB", 57, "14 seconds"));
            reported.Should().NotBeEmpty().And.OnlyContain(line => line.Kind == JobProgressKind.ToolOutput);
            reported.Should().Contain(JobProgress.Tool("crf 32 VMAF 93.44 predicted video stream size 37.07 MiB (57%) taking 14 seconds"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ListProgress(List<JobProgress> lines) : IProgress<JobProgress>
    {
        // stdout and stderr are read on separate tasks.
        public void Report(JobProgress value) { lock (lines) lines.Add(value); }
    }
}
