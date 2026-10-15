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
    public void WithoutALimitTheIntroAsksForEveryFittingVideoTheDownloadsAreExplainedOnceAndTheQueuedOutcomeHasNoCount()
    {
        var (console, output, _) = ImmichConsole();

        console.Report(new ImmichDiscoveryProgress.Started(null, 0, 2));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(1, "a.mp4", Taken));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, 1, Facts("hevc", 1920, 1080, 8_100_000, 10), 2));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(2, "b.mp4", Taken));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, 2, Facts("hevc", 1920, 1080, 8_100_000, 10), 1));

        output.ToString().Should().Be(
            "Looking in Immich for every video that fits your rules.\n" +
            "Each video below is downloaded and checked. If it doesn't fit your rules, the download is deleted and the video is not checked again until your rules change.\n" +
            "\n" +
            "  1. a.mp4  2022-11-30  hevc 1080p 8.1 Mbps  10 B  queued: fits rule 2\n" +
            "  2. b.mp4  2022-11-30  hevc 1080p 8.1 Mbps  10 B  queued: fits rule 1\n");
    }

    /// <summary>The line reads for one video as well as many, and the stale count is of videos checked again, not of a rule change.</summary>
    [Theory]
    [InlineData(1, "1 video that didn't fit your earlier rules is checked again under the current ones when the search reaches it.")]
    [InlineData(58, "58 videos that didn't fit your earlier rules are checked again under the current ones when the search reaches them.")]
    public void TheStaleSkipsAreSaidRightAfterTheIntroAndBeforeAnyDownload(int stale, string line)
    {
        var (console, output, _) = ImmichConsole();

        console.Report(new ImmichDiscoveryProgress.Started(3, stale, 1));

        output.ToString().Should().Be("Looking in Immich for up to 3 new videos to convert.\n" + line + "\n");
    }

    [Fact]
    public void WithOneRuleTheRuleNumberIsLeftOutOfBothOutcomes()
    {
        var (console, output, _) = ImmichConsole();

        console.Report(new ImmichDiscoveryProgress.Started(5, 0, 1));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(1, "a.mp4", Taken));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, 1, Facts("hevc", 1920, 1080, 20_000_000, 10), 1));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(2, "b.mp4", Taken));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Skipped, "needs 10Mbps", null, 0, Facts("hevc", 1920, 1080, 6_000_000, 10)));

        output.ToString().Should().EndWith(
            "  1. a.mp4  2022-11-30  hevc 1080p 20 Mbps  10 B  queued (1 of 5)\n" +
            "  2. b.mp4  2022-11-30  hevc 1080p 6.0 Mbps  10 B  doesn't fit: needs 10Mbps\n");
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
        console.Report(new ImmichDiscoveryProgress.Started(null, 0, 1));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(1, "a.mp4", Taken));

        // The file and date are on screen while the download runs.
        output.ToString().Should().EndWith("  1. a.mp4  2022-11-30  ");

        console.Report(new ImmichProgress.AssetIssue("b.txt", "the name Immich gives it can't be saved safely on this PC; left alone"));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, 1, Facts("hevc", 1920, 1080, 20_000_000, 2048), 1));

        output.ToString().Should().EndWith(
            "  1. a.mp4  2022-11-30  \n" +
            "Issue: b.txt: the name Immich gives it can't be saved safely on this PC; left alone.\n" +
            "  1. a.mp4  2022-11-30  hevc 1080p 20 Mbps  2.0 KiB  queued\n");
    }

    [Fact]
    public void ADownloadCutShortEndsItsLineBeforeTheErrorThatFollows()
    {
        var (console, output, _) = ImmichConsole();
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(1, "a.mp4", Taken));

        console.FinishLine();
        console.FinishLine();

        output.ToString().Should().EndWith("  1. a.mp4  2022-11-30  stopped\n");
    }

    [Fact]
    public void TheDiscoverySummaryNamesTheSkipReasonsWithTheirCountsAndGroupsWhatWasNotDownloaded()
    {
        var result = Result(queued: 1, skipped: 2, reasons: [new SkipReasonCount("rule 1 needs 2GiB", 2)],
            alreadyConverted: 5, previouslyFailed: 1, previouslySkipped: 48, ineligible: 193);

        ImmichDiscoveryConsole.Summary(result, 1).Should().Equal(
            "",
            "Downloaded and checked 3 videos: 1 queued, 2 don't fit your rules (rule 1 needs 2GiB).",
            "Not downloaded: 54 handled in earlier runs, 193 too small or not a file type your rules list.");
    }

    [Fact]
    public void WithSeveralReasonsTheSummaryNamesTheThreeMostCommonAndCountsTheRest()
    {
        var result = Result(queued: 5, skipped: 20,
            reasons: [new SkipReasonCount("no rule for vp9", 9), new SkipReasonCount("rule 1 needs 2GiB", 5), new SkipReasonCount("rule 2 needs 10Mbps", 3), new SkipReasonCount("rule 2 needs 300MiB", 2), new SkipReasonCount("rule 3 needs 4k+", 1)]);

        ImmichDiscoveryConsole.Summary(result, 5).Should().Equal(
            "",
            "Downloaded and checked 25 videos: 5 queued, 20 don't fit your rules (9 no rule for vp9, 5 rule 1 needs 2GiB, 3 rule 2 needs 10Mbps, 3 other).");
    }

    /// <summary>
    /// A search stops at its limit, so a count below it means Immich had no more. Without a limit nothing is said, and a
    /// search that downloaded nothing prints no checked line and no blank line to set it apart.
    /// </summary>
    [Fact]
    public void TheSummaryOfASearchThatRanOutSaysSoAndKeepsTheIssueCount()
    {
        ImmichDiscoveryConsole.Summary(Result(queued: 0, skipped: 1, reasons: [new SkipReasonCount("no rule for h264", 1)], issues: 1), 3).Should().Equal(
            "",
            "Downloaded and checked 1 video: 1 doesn't fit your rules (no rule for h264).",
            "Immich has no more videos that fit your rules.",
            "Also: 1 issue (above).");
        ImmichDiscoveryConsole.Summary(Result(queued: 1, skipped: 0, reasons: []), null).Should().Equal(
            "",
            "Downloaded and checked 1 video: 1 queued.");
        ImmichDiscoveryConsole.Summary(Result(queued: 0, skipped: 0, reasons: []), 3).Should().Equal("Immich has no more videos that fit your rules.");
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
            AlreadyQueued: 2, Issues: 0, CacheHits: 5, RealProbes: 4, Held: 1, ReadyToFinalize: 2), CliCommand.Parse([]));

        output.ToString().Should().Be(
            $"Queued in {first}:\n" +
            "  a.mp4\n" +
            "  b.mp4\n" +
            $"Queued in {second}:\n" +
            "  c.mp4\n" +
            "Queued: 3. Already queued: 2. Held: 1. Ready to finalise: 2. Cache hits: 5. Real probes: 4.\n");
    }

    /// <summary>The notes under the counts suggest commands, which carry the --config the user typed.</summary>
    [Fact]
    public void TheNotesUnderTheCountsNameTheirCommandsWithTheTypedConfig()
    {
        var output = new StringWriter { NewLine = "\n" };

        QueueDiscoverConsole.WriteCounts(output, new QueueDiscoveryResult([], AlreadyQueued: 0, Issues: 0, PreviouslyFailed: 1, PreviouslySkipped: 1), CliCommand.Parse(["queue", "add", "--config", "my.yaml"]));

        output.ToString().Should().EndWith(
            "Failed or cancelled originals stay out of the queue until they change. 'video-optimiser history --config \"my.yaml\"' lists their jobs; 'video-optimiser queue retry <job-id> --config \"my.yaml\"' queues one again.\n" +
            "Skipped originals (AV1 would not save enough) are checked again when the file or the quality settings change; 'video-optimiser queue retry <job-id> --config \"my.yaml\"' queues one now.\n");
    }

    private static CandidateFacts Facts(string codec, int width, int height, long bitsPerSecond, long sizeBytes) => new(codec, width, height, bitsPerSecond, sizeBytes);

    private static (ImmichDiscoveryConsole Console, StringWriter Output, StringWriter Error) ImmichConsole()
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        return (new ImmichDiscoveryConsole(output, TerminalStyle.Plain, error, TerminalStyle.Plain), output, error);
    }

    private static ImmichDiscoveryResult Result(int queued, int skipped, SkipReasonCount[] reasons, int alreadyConverted = 0, int previouslyFailed = 0, int previouslySkipped = 0, int ineligible = 0, int issues = 0) =>
        new([.. Enumerable.Range(0, queued).Select(_ => new JobRecord())], 0, 0, 0, alreadyConverted, previouslyFailed, previouslySkipped, 0, ineligible, skipped, issues, reasons);

    private static ScanProgress Checked(int files, int probed) => new("x.mp4", "Checked", string.Empty, files, probed);

    private sealed class FakeClock
    {
        private DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Now() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
