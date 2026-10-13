using FluentAssertions;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// The text a user or an agent reads from the commands other than a job's own progress: Immich discovery, file discovery,
/// finalisation and cleanup. Each is rendered by a small class in the Cli so the layout is tested without the command.
/// </summary>
public sealed class HumanOutputConsoleTests
{
    private static readonly DateTime Taken = new(2022, 11, 30, 21, 37, 45);

    [Fact]
    public void ImmichDiscoveryReadsAsAnIntroOneLinePerDownloadWithItsFactsAndASummary()
    {
        var (console, output, error) = ImmichConsole();

        console.Report(new ImmichDiscoveryProgress.Started(1, 0, 418, 3));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(1, "PXL_20250718_192844658.mp4", new DateTime(2025, 7, 18)));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Skipped, "rule 1 needs 2GiB", null, 0, Facts("h264", 3840, 2160, 41_700_000, 519_200_000)));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(2, "PXL_20260223_092415845.mp4", new DateTime(2026, 2, 23)));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, 1, Facts("hevc", 3840, 2160, 33_000_000, 334_900_000), 3));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(3, "VID_x.mp4", new DateTime(2023, 1, 2)));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Issue, "the download does not match Immich's checksum", "it will be tried again next run", 0));
        console.Report(new ImmichDiscoveryProgress.AssetExcluded("fav.mp4", "0f1e", "is a favourite"));

        output.ToString().Should().Be(
            "Searching Immich for 1 video to convert. Each candidate is downloaded and checked with\n" +
            "ffprobe; ones that don't fit your rules are deleted and skipped in future runs.\n" +
            "Your eligibility rules changed since the last run, so 418 videos skipped before will be downloaded and checked again.\n" +
            "\n" +
            "[1] PXL_20250718_192844658.mp4  2025-07-18  h264 4K 42 Mbps  495.1 MiB  skip: rule 1 needs 2GiB\n" +
            "[2] PXL_20260223_092415845.mp4  2026-02-23  hevc 4K 33 Mbps  319.4 MiB  queued: fits rule 3 (1 of 1)\n" +
            "[3] VID_x.mp4  2023-01-02  issue: the download does not match Immich's checksum; it will be tried again next run\n");
        error.ToString().Should().Be("Excluded: fav.mp4 (asset 0f1e) is a favourite; left alone.\n");
    }

    [Fact]
    public void WithoutALimitTheIntroAsksForEveryFittingVideoNamesOpenJobsAndTheQueuedOutcomeHasNoCount()
    {
        var (console, output, _) = ImmichConsole();

        console.Report(new ImmichDiscoveryProgress.Started(null, 2, 0, 2));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(1, "a.mp4", Taken));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, 3, Facts("hevc", 1920, 1080, 8_100_000, 10), 2));

        output.ToString().Should().StartWith(
                "Searching Immich for every video that fits your rules. Each candidate is downloaded and checked with\n" +
                "ffprobe; ones that don't fit your rules are deleted and skipped in future runs.\n" +
                "2 already queued from earlier runs.\n")
            .And.NotContain("rules changed")
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
    [InlineData(3840, 2160, "4K")]
    [InlineData(2160, 3840, "4K")]
    [InlineData(2560, 1440, "1440p")]
    [InlineData(1440, 2560, "1440p")]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(1080, 1920, "1080p")]
    [InlineData(1280, 720, "720p")]
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

    [Theory]
    [InlineData(41_700_000L, "42 Mbps")]
    [InlineData(10_000_000L, "10 Mbps")]
    [InlineData(9_960_000L, "10 Mbps")]
    [InlineData(8_140_000L, "8.1 Mbps")]
    [InlineData(500_000L, "0.5 Mbps")]
    public void BitrateIsWholeMbpsWithOneDecimalUnderTen(long bitsPerSecond, string expected) =>
        ImmichDiscoveryConsole.Bitrate(bitsPerSecond).Should().Be(expected);

    [Fact]
    public void ALineIsNotBrokenByOtherOutputAndItsOutcomeThenRepeatsTheFile()
    {
        var (console, output, _) = ImmichConsole();
        console.Report(new ImmichDiscoveryProgress.Started(null, 0, 0, 1));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(1, "a.mp4", Taken));

        // The file and date are on screen while the download runs.
        output.ToString().Should().EndWith("[1] a.mp4  2022-11-30  ");

        console.Report(new ImmichDiscoveryProgress.AssetIssue("b.txt", "asset 1 has an ID or file name that cannot be staged safely; it is left alone"));
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
    public void RestagingReadsAsAStepWithAnOutcome()
    {
        var (console, output, _) = ImmichConsole();
        var job = Guid.Parse("11111111-2222-3333-4444-555555555555");

        console.Report(new ImmichDiscoveryProgress.Restaging("a.mp4", job));
        console.Report(new ImmichDiscoveryProgress.Restaged("a.mp4", null));
        console.Report(new ImmichDiscoveryProgress.Restaging("b.mp4", job));
        console.Report(new ImmichDiscoveryProgress.Restaged("b.mp4", "Immich did not return the video again; the job is marked failed"));

        output.ToString().Should().Be(
            "Downloading again: a.mp4 (job 11111111222233334444555555555555 is open but its staged copy is missing)\n" +
            "  done: the staged copy is back\n" +
            "Downloading again: b.mp4 (job 11111111222233334444555555555555 is open but its staged copy is missing)\n" +
            "  issue: Immich did not return the video again; the job is marked failed\n");
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
    public void TheDiscoverySummaryIsOneLineWhenThereIsNothingElseToSay()
    {
        ImmichDiscoveryConsole.Summary(Result(queued: 0, alreadyQueued: 0, skipped: 0, reasons: []), 3)
            .Should().Equal("Found 0 of 3 after checking 0.");
    }

    [Fact]
    public void TheGuardSaysNothingWhenItFoundNothingAndOtherwiseWhatItDid()
    {
        ReuploadGuardReport.Summary(Guard(4)).Should().BeNull();
        ReuploadGuardReport.Summary(Guard(1, trashed: 1)).Should().Be("Re-upload guard: 1 converted original was uploaded again and moved to Immich's trash.");
        ReuploadGuardReport.Summary(Guard(7, trashed: 2, restored: 1, issues: 2)).Should().Be(
            "Re-upload guard: 2 converted originals were uploaded again and moved to Immich's trash; 1 converted original is out of Immich's trash, so both versions exist; 2 issues.");
    }

    [Fact]
    public void GuardReportsUseSentenceCaseNotShouting()
    {
        ReuploadGuardReport.FormatProgress(new ScanProgress("a.mp4", "Trashed", "a.mp4 (taken 2022-11-30 21:37) is a converted original the phone uploaded again.")).Should()
            .Be("Re-upload guard: trashed a.mp4 (taken 2022-11-30 21:37) is a converted original the phone uploaded again.");
        ReuploadGuardReport.FormatProgress(new ScanProgress("a.mp4", "Restored", "a.mp4 is out of the trash.")).Should().Be("Re-upload guard: restored a.mp4 is out of the trash.");
        ReuploadGuardReport.FormatProgress(new ScanProgress("asset", "Issue", "Could not read asset 1.")).Should().Be("Re-upload guard issue: Could not read asset 1.");
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
    public void FileDiscoveryGroupsQueuedFilesByFolderAndPointsToTheNextCommand()
    {
        var first = Path.Combine(Path.GetTempPath(), "videos", "2022");
        var second = Path.Combine(Path.GetTempPath(), "videos", "2023");
        var output = new StringWriter { NewLine = "\n" };

        QueueDiscoverConsole.WriteSummary(output, new QueueDiscoveryResult(
            [Path.Combine(first, "a.mp4"), Path.Combine(second, "c.mp4"), Path.Combine(first, "b.mp4")],
            AlreadyQueued: 2, Issues: 0, CacheHits: 5, RealProbes: 4));

        output.ToString().Should().Be(
            $"Queued in {first}:\n" +
            "  a.mp4\n" +
            "  b.mp4\n" +
            $"Queued in {second}:\n" +
            "  c.mp4\n" +
            "Queued: 3. Already queued: 2. Cache hits: 5. Real probes: 4.\n" +
            "Next: run 'video-optimiser queue run'.\n");
    }

    [Fact]
    public void FileDiscoverySuggestsNoNextStepWhenNothingIsQueued()
    {
        var output = new StringWriter { NewLine = "\n" };

        QueueDiscoverConsole.WriteSummary(output, new QueueDiscoveryResult([], AlreadyQueued: 0, Issues: 0, CacheHits: 2));

        output.ToString().Should().Be("Queued: 0. Cache hits: 2.\n");
    }

    [Fact]
    public void FinalizeListsEachReadyJobByFileWithItsSizesAndSavings()
    {
        FinalizationConsole.DescribeReady(Job("D:\\v\\clip.mp4", before: 1L << 30, after: 400L << 20, saved: 61.1m)).Should().Be("clip.mp4  1.0 GiB -> 400.0 MiB, 61.1% saved");
        FinalizationConsole.DescribeReady(Job("D:\\v\\old.mp4")).Should().Be("old.mp4  sizes unknown");
    }

    [Fact]
    public void FinalizeReportsEachJobThenTheTallyAndRepeatsOnlyTheFailuresWithTheirIds()
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var console = new FinalizationConsole(output, error);
        var done = Job("D:\\v\\a.mp4");
        var failed = Job("D:\\v\\b.mp4");

        console.Outcome(done, new FinalizationResult(done, ExitCode.Success, "Installed the AV1 and archived the original.")).Should().BeTrue();
        console.Outcome(failed, new FinalizationResult(failed, ExitCode.FinalisationFailure, "The AV1 changed since validation.")).Should().BeFalse();
        console.Summary(2, [(failed, "The AV1 changed since validation.")]);

        output.ToString().Should().Be(
            "Done    a.mp4: Installed the AV1 and archived the original.\n" +
            "Finalised: 1. Failed: 1.\n" +
            $"{failed.Id:N}  b.mp4  The AV1 changed since validation.\n");
        error.ToString().Should().Be("Failed  b.mp4: The AV1 changed since validation.\n");
    }

    [Fact]
    public void CleanupPrintsEachFolderOnceWithItsFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "videos", ".video-optimiser");
        var staging = Path.Combine(Path.GetTempPath(), "staging", "0f1e");
        var output = new StringWriter { NewLine = "\n" };
        var plan = new TemporaryOutputCleanupPlan(
            [new AbandonedTemporaryOutput(Path.Combine(folder, "a.encoding.mp4"), Guid.NewGuid(), 1, 3L << 20, "job failed"), new AbandonedTemporaryOutput(Path.Combine(folder, "b.encoding.mp4"), Guid.NewGuid(), 2, 1L << 30, "job cancelled")],
            [new StagedImmichOriginal(Path.Combine(staging, "PXL.mp4"), Guid.NewGuid(), 500L << 20, "job completed")],
            []);

        CleanupReport.WriteFiles(output, plan);

        output.ToString().Should().Be(
            $"{folder}\n" +
            "     3.0 MiB  a.encoding.mp4 (job failed)\n" +
            "     1.0 GiB  b.encoding.mp4 (job cancelled)\n" +
            $"{staging}\n" +
            "   500.0 MiB  PXL.mp4 (job completed)\n");
    }

    private static CandidateFacts Facts(string codec, int width, int height, long bitsPerSecond, long sizeBytes) => new(codec, width, height, bitsPerSecond, sizeBytes);

    private static (ImmichDiscoveryConsole Console, StringWriter Output, StringWriter Error) ImmichConsole()
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        return (new ImmichDiscoveryConsole(output, error), output, error);
    }

    private static ImmichDiscoveryResult Result(int queued, int alreadyQueued, int skipped, SkipReasonCount[] reasons, int restaged = 0, int alreadyConverted = 0, int previouslyFailed = 0, int previouslySkipped = 0, int excluded = 0, int ineligible = 0, int issues = 0) =>
        new([.. Enumerable.Range(0, queued).Select(_ => new JobRecord())], restaged, alreadyQueued, alreadyConverted, previouslyFailed, previouslySkipped, excluded, ineligible, skipped, issues, reasons);

    private static ImmichReuploadGuardResult Guard(int checkedCount, int trashed = 0, int restored = 0, int issues = 0) =>
        new(checkedCount,
            [.. Enumerable.Range(0, trashed).Select(_ => new ImmichReuploadHit(Guid.NewGuid(), "a", "a.mp4", Taken))],
            [.. Enumerable.Range(0, restored).Select(_ => new ImmichReuploadHit(Guid.NewGuid(), "a", "a.mp4", Taken))],
            [.. Enumerable.Range(0, issues).Select(_ => new ImmichReuploadIssue(Guid.NewGuid(), "a", "reason"))]);

    private static ScanProgress Checked(int files, int probed) => new("x.mp4", "Checked", string.Empty, files, probed);

    private static JobRecord Job(string path, long? before = null, long? after = null, decimal? saved = null) =>
        new() { Id = Guid.NewGuid(), SourcePath = path, SourceSizeBytes = before, OutputSizeBytes = after, PercentageSaved = saved };

    private sealed class FakeClock
    {
        private DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Now() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
