using FluentAssertions;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.ImmichRun;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// The text a user or an agent reads from the commands other than a job's own progress: Immich discovery and file discovery.
/// Each is rendered by a small class in the Cli so the layout is tested without the command. Finalisation and the run tally
/// are pinned by the command transcripts and <c>ColourOutputTests</c>.
/// </summary>
public sealed class HumanOutputConsoleTests
{
    private static readonly DateTime Taken = new(2022, 11, 30, 21, 37, 45);

    [Fact]
    public void WithoutALimitTheIntroAsksForEveryFittingVideoNamesOpenJobsAndTheQueuedOutcomeHasNoCount()
    {
        var (console, output, _) = ImmichConsole();

        console.Report(new ImmichDiscoveryProgress.Started(null, 2, 0, 2));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(1, "a.mp4", Taken));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, 3, Facts("hevc", 1920, 1080, 8_100_000, 10), 2));

        output.ToString().Should().StartWith(
                "Searching Immich for every video that fits your rules.\n" +
                "Each candidate is downloaded and checked with ffprobe.\n" +
                "If it doesn't fit your rules, the download is deleted and the video is skipped in future runs.\n" +
                "2 already queued from earlier runs.\n")
            .And.NotContain("different eligibility rules")
            .And.EndWith("[1] a.mp4  2022-11-30  hevc 1080p 8.1 Mbps  10 B  queued: fits rule 2\n");
    }

    [Fact]
    public void WithOneRuleTheRuleNumberIsLeftOutOfBothOutcomes()
    {
        var (console, output, _) = ImmichConsole();

        console.Report(new ImmichDiscoveryProgress.Started(5, 0, 0, 1));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(1, "a.mp4", Taken));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, 1, Facts("hevc", 1920, 1080, 20_000_000, 10), 1));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(2, "b.mp4", Taken));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Skipped, "needs 10Mbps", null, 0, Facts("hevc", 1920, 1080, 6_000_000, 10)));

        output.ToString().Should().EndWith(
            "[1] a.mp4  2022-11-30  hevc 1080p 20 Mbps  10 B  queued (1 of 5)\n" +
            "[2] b.mp4  2022-11-30  hevc 1080p 6.0 Mbps  10 B  skip: needs 10Mbps\n");
    }

    [Theory]
    [InlineData(1440, 2560, "1440p")]
    [InlineData(720, 1280, "720p")]
    [InlineData(640, 480, "640x480")]
    public void ResolutionIsNamedByTheShorterSideSoPortraitAndLandscapeReadAlike(int width, int height, string expected) =>
        ImmichDiscoveryConsole.Resolution(width, height).Should().Be(expected);

    [Fact]
    public void AnUnknownResolutionOrBitrateReadsAsAQuestionMark()
    {
        ImmichDiscoveryConsole.Resolution(null, 1080).Should().Be("?");
        ImmichDiscoveryConsole.Resolution(1920, null).Should().Be("?");
        ImmichDiscoveryConsole.Bitrate(null).Should().Be("? Mbps");
    }

    [Fact]
    public void BitrateRoundsToTenBeforeItDropsTheDecimal() =>
        ImmichDiscoveryConsole.Bitrate(9_960_000L).Should().Be("10 Mbps");

    [Fact]
    public void ALineIsNotBrokenByOtherOutputAndItsOutcomeThenRepeatsTheFile()
    {
        var (console, output, _) = ImmichConsole();
        console.Report(new ImmichDiscoveryProgress.Started(null, 0, 0, 1));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(1, "a.mp4", Taken));

        // The file and date are on screen while the download runs.
        output.ToString().Should().EndWith("[1] a.mp4  2022-11-30  ");

        console.Report(new ImmichProgress.AssetIssue("b.txt", "asset 1 has an ID or file name that cannot be staged safely; it is left alone"));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, 1, Facts("hevc", 1920, 1080, 20_000_000, 2048), 1));

        output.ToString().Should().EndWith(
            "[1] a.mp4  2022-11-30  \n" +
            "Issue: b.txt: asset 1 has an ID or file name that cannot be staged safely; it is left alone.\n" +
            "[1] a.mp4  2022-11-30  hevc 1080p 20 Mbps  2.0 KiB  queued\n");
    }

    [Fact]
    public void ADownloadCutShortEndsItsLineBeforeTheErrorThatFollows()
    {
        var (console, output, _) = ImmichConsole();
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(1, "a.mp4", Taken));

        console.FinishLine();
        console.FinishLine();

        output.ToString().Should().Be("[1] a.mp4  2022-11-30  stopped\n");
    }

    [Fact]
    public void TheDiscoverySummaryNamesTheSkipReasonsWithTheirCountsAndGroupsWhatWasNotDownloaded()
    {
        var result = Result(queued: 1, alreadyQueued: 0, skipped: 2, reasons: [new SkipReasonCount("rule 1 needs 2GiB", 2)],
            alreadyConverted: 5, previouslyFailed: 1, previouslySkipped: 48, ineligible: 193);

        ImmichDiscoveryConsole.Summary(result, 1).Should().Equal(
            "Found 1 of 1 after checking 3. Skipped 2: rule 1 needs 2GiB.",
            "Not downloaded: 54 handled in earlier runs, 193 too small or not a listed file type.");
    }

    [Fact]
    public void WithSeveralReasonsTheSummaryNamesTheThreeMostCommonAndCountsTheRest()
    {
        var result = Result(queued: 5, alreadyQueued: 0, skipped: 20, excluded: 1,
            reasons: [new SkipReasonCount("no rule for vp9", 9), new SkipReasonCount("rule 1 needs 2GiB", 5), new SkipReasonCount("rule 2 needs 10Mbps", 3), new SkipReasonCount("rule 2 needs 300MiB", 2), new SkipReasonCount("rule 3 needs 4k+", 1)]);

        ImmichDiscoveryConsole.Summary(result, 5).Should().Equal(
            "Found 5 of 5 after checking 25. Skipped 20: 9 no rule for vp9, 5 rule 1 needs 2GiB, 3 rule 2 needs 10Mbps, 3 other.",
            "Not downloaded: 1 excluded.");
    }

    [Fact]
    public void TheDiscoverySummaryWithoutALimitCountsOpenJobsAndKeepsRestagedAndIssueCounts()
    {
        var result = Result(queued: 1, alreadyQueued: 2, skipped: 0, reasons: [], restaged: 2, issues: 1);

        ImmichDiscoveryConsole.Summary(result, null).Should().Equal(
            "Found 3 videos to convert after checking 1.",
            "Also: 2 staged originals downloaded again, 1 issue (above).");
        ImmichDiscoveryConsole.Summary(Result(queued: 0, alreadyQueued: 0, skipped: 0, reasons: [], restaged: 1), 3).Should().Equal(
            "Found 0 of 3 after checking 0.",
            "Also: 1 staged original downloaded again.");
    }

    [Fact]
    public void FileDiscoveryIsSilentForTheFirstTenSecondsThenCountsAtMostEveryTenSeconds()
    {
        var clock = new FakeClock();
        var output = new StringWriter { NewLine = "\n" };
        var console = new QueueDiscoverConsole(output, clock.Now);

        console.Report(Checked(1, 0));
        clock.Advance(TimeSpan.FromSeconds(9));
        console.Report(Checked(40, 3));
        output.ToString().Should().BeEmpty();

        clock.Advance(TimeSpan.FromSeconds(1));
        console.Report(Checked(55, 5));
        clock.Advance(TimeSpan.FromSeconds(4));
        console.Report(Checked(60, 6));
        clock.Advance(TimeSpan.FromSeconds(6));
        console.Report(Checked(90, 9));

        output.ToString().Should().Be(
            "Checked 55 files so far (5 probed with ffprobe)...\n" +
            "Checked 90 files so far (9 probed with ffprobe)...\n");
    }

    [Fact]
    public void FileDiscoveryNamesAnIssueByFileUnderItsFolderPrintedOnce()
    {
        var output = new StringWriter { NewLine = "\n" };
        var console = new QueueDiscoverConsole(output, new FakeClock().Now);
        var folder = Path.Combine(Path.GetTempPath(), "videos");

        console.Report(new ScanProgress(Path.Combine(folder, "a.mp4"), "Issue", "ffprobe exited with 1"));
        console.Report(new ScanProgress(Path.Combine(folder, "b.mp4"), "Issue", "File is still being written."));
        console.Report(new ScanProgress(string.Empty, "Issue", "Watch root does not exist: D:\\gone"));

        output.ToString().Should().Be(
            $"In {folder}:\n" +
            "  Issue: a.mp4: ffprobe exited with 1\n" +
            "  Issue: b.mp4: File is still being written.\n" +
            "Issue: Watch root does not exist: D:\\gone\n");
    }

    [Fact]
    public void FileDiscoveryGroupsQueuedFilesByFolderAndCountsHeldAndReadyApart()
    {
        var first = Path.Combine(Path.GetTempPath(), "videos", "2022");
        var second = Path.Combine(Path.GetTempPath(), "videos", "2023");
        var output = new StringWriter { NewLine = "\n" };

        QueueDiscoverConsole.WriteSummary(output, new QueueDiscoveryResult(
            [Path.Combine(first, "a.mp4"), Path.Combine(second, "c.mp4"), Path.Combine(first, "b.mp4")],
            AlreadyQueued: 2, Issues: 0, CacheHits: 5, RealProbes: 4, Held: 1, ReadyToFinalize: 2));

        output.ToString().Should().Be(
            $"Queued in {first}:\n" +
            "  a.mp4\n" +
            "  b.mp4\n" +
            $"Queued in {second}:\n" +
            "  c.mp4\n" +
            "Queued: 3. Already queued: 2. Held: 1. Ready to finalise: 2. Cache hits: 5. Real probes: 4.\n");
    }

    private static CandidateFacts Facts(string codec, int width, int height, long bitsPerSecond, long sizeBytes) => new(codec, width, height, bitsPerSecond, sizeBytes);

    private static (ImmichDiscoveryConsole Console, StringWriter Output, StringWriter Error) ImmichConsole()
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        return (new ImmichDiscoveryConsole(output, TerminalStyle.Plain, error, TerminalStyle.Plain), output, error);
    }

    private static ImmichDiscoveryResult Result(int queued, int alreadyQueued, int skipped, SkipReasonCount[] reasons, int restaged = 0, int alreadyConverted = 0, int previouslyFailed = 0, int previouslySkipped = 0, int excluded = 0, int ineligible = 0, int issues = 0) =>
        new([.. Enumerable.Range(0, queued).Select(_ => new JobRecord())], restaged, alreadyQueued, alreadyConverted, previouslyFailed, previouslySkipped, excluded, ineligible, skipped, issues, reasons);

    private static ScanProgress Checked(int files, int probed) => new("x.mp4", "Checked", string.Empty, files, probed);

    private sealed class FakeClock
    {
        private DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Now() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
