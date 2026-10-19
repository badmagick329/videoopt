using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Calibrate;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.UnitTests.Features.Calibrate;

public sealed class CalibrationReportTests
{
    private const string Current = "current-settings";
    private const long GiB = 1L << 30;

    [Fact]
    public void EachVideoGoesUnderTheFirstRuleItMatchesOrIntoAGroupByCodecResolutionAndBitrate()
    {
        var report = Build([H264Rule(), H264Rule()], [
            Video("admitted.mp4", "h264", 1920, 1080, 20),
            Video("slow.mp4", "h264", 1920, 1080, 12),
            Video("slow2.mp4", "h264", 2000, 1100, 14.9),
            Video("hevc.mp4", "hevc", 3840, 2160, 25),
            Video("portrait.mp4", "h264", 1080, 1440, 8),
            Video("unknown.mkv", "hevc", 3840, 2160, null)]);

        report.Rules.Select(row => (row.Number, row.Videos, row.Bytes)).Should().Equal((1, 1, 2 * GiB), (2, 0, 0L));
        report.KeptOut.Select(Describe).Should().BeEquivalentTo(
            "h264 1080p-1440p 10-15 2", "hevc 4k+ 20-30 1", "h264 <1080p 5-10 1", "hevc 4k+ ? 1");
        report.CodecUnknown.Should().BeEmpty();
    }

    [Fact]
    public void AnImmichAssetWithoutACodecIsGroupedApartEvenWhenItsNumbersFitARule()
    {
        var report = Build([H264Rule()], [Immich("PXL_1.mp4", null, 1920, 1080, 30), Immich("PXL_2.mp4", null, 1920, 1080, 31)]);

        report.Rules[0].Videos.Should().Be(0);
        report.KeptOut.Should().BeEmpty();
        report.CodecUnknown.Select(Describe).Should().Equal(" 1080p-1440p 30-40 2");
    }

    [Fact]
    public void AV1VideosAndConvertedImmichAssetsAreCountedAsAlreadyAV1()
    {
        var report = Build([H264Rule()], [
            Video("output.mkv", "av1", 3840, 2160, 10),
            Immich("replacement.mp4", null, 3840, 2160, 10) with { AlreadyConverted = true }]);

        (report.AlreadyAv1Videos, report.AlreadyAv1Bytes).Should().Be((2, 4 * GiB));
        report.KeptOut.Should().BeEmpty();
        report.CodecUnknown.Should().BeEmpty();
    }

    [Theory]
    [InlineData(4_999_999L, 0, 5)]
    [InlineData(5_000_000L, 5, 10)]
    [InlineData(100_000_000L, 100, null)]
    public void BitrateBandsStartAtTheirLowerEdge(long bitsPerSecond, int minimum, int? maximum) =>
        BitrateBand.For(bitsPerSecond).Should().Be(new BitrateBand(minimum, maximum));

    [Theory]
    [InlineData(1920, 1079, "<1080p")]
    [InlineData(2560, 1440, "1440p-4k")]
    public void ResolutionBandsUseTheRulesPixelCounts(int width, int height, string band) =>
        ResolutionBand.For(width, height).Label.Should().Be(band);

    [Fact]
    public void OnlyCompletedAndSkippedJobsAtTheCurrentSettingsCountAsHistory()
    {
        var report = Build([H264Rule()], [], [
            Result(true, 40, saved: 71.4m),
            Result(true, 45, saved: 80m),
            Result(false, 20),
            Result(true, 50, fingerprint: "other-settings"),
            Result(true, 50, fingerprint: null),
            Change(Result(true, 50), job => job.VideoFacts = null),
            Change(Result(true, 50), job => job.Status = JobStatus.Failed),
            Change(Result(true, 50), job => job.Status = JobStatus.Cancelled),
            Change(Result(false, 50), job => job.FailureCategory = FailureCategories.UnsupportedAudio)]);

        (report.OtherSettings, report.NoVideoFacts).Should().Be((2, 1));
        report.Rules[0].History.Should().Be(new CalibrationHistory(2, new IntRange(40, 45), new IntRange(71, 80), 1, new IntRange(20, 20)));
    }

    [Fact]
    public void AResultCountsUnderTheRuleTodaysSettingsWouldGiveItsVideo()
    {
        var report = Build([H264Rule("15Mbps")], [], [Result(true, 12, saved: 60m)]);

        report.Rules[0].History.Should().BeNull();
        report.KeptOut.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Codec = "h264", Videos = 0, History = new CalibrationHistory(1, new IntRange(12, 12), new IntRange(60, 60), 0, null) });
    }

    [Fact]
    public void ARuleWhoseNotWorthItResultsAllSitBelowItsConversionsGetsTheNextBandEdge()
    {
        var report = Build([H264Rule()], [], [Result(false, 20), Result(false, 22), Result(false, 25), Result(true, 40), Result(true, 45)]);

        report.Suggestions.Should().Equal(new RaiseMinimumBitrateSuggestion(1, 30, 3, new IntRange(20, 25), 2, 40));
    }

    [Fact]
    public void ARaiseFallsBackToTheNextWholeMbpsWhenNoBandEdgeFitsBelowTheLowestConversion()
    {
        var report = Build([H264Rule()], [], [Result(false, 20), Result(false, 21), Result(false, 22.3), Result(true, 24)]);

        report.Suggestions.Should().ContainSingle().Which.Should().BeOfType<RaiseMinimumBitrateSuggestion>().Which.MinimumMbps.Should().Be(23);
    }

    [Fact]
    public void ARaiseNeedsNoConversions()
    {
        var report = Build([H264Rule()], [], [Result(false, 16), Result(false, 17), Result(false, 18)]);

        report.Suggestions.Should().Equal(new RaiseMinimumBitrateSuggestion(1, 20, 3, new IntRange(16, 18), 0, null));
    }

    [Fact]
    public void NoRaiseRestsOnFewerThanThreeNotWorthItResults()
    {
        var report = Build([H264Rule()], [], [Result(false, 20), Result(false, 25), Result(true, 40), Result(true, 45), Result(true, 50)]);

        report.Suggestions.Should().BeEmpty();
    }

    [Fact]
    public void NoRaiseWhenAConversionSitsAmongTheNotWorthItResults()
    {
        var report = Build([H264Rule()], [], [Result(false, 20), Result(false, 25), Result(false, 30), Result(true, 22)]);

        report.Suggestions.Should().BeEmpty("no bitrate separates the two outcomes");
    }

    [Fact]
    public void AKeptOutGroupWhereMostResultsConvertedGetsARuleSuggestion()
    {
        var report = Build([H264Rule()], [], [Result(true, 45, "hevc", 3840, 2160, 41m), Result(true, 58, "hevc", 3840, 2160, 55m), Result(false, 41, "hevc", 3840, 2160)]);

        var suggestion = report.Suggestions.Should().ContainSingle().Which.Should().BeOfType<AddRuleSuggestion>().Subject;
        suggestion.MinimumMbps.Should().Be(40);
        Describe(suggestion.Group).Should().Be("hevc 4k+ 40-60 0");
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 2)]
    public void NoRuleSuggestionFromFewerThanThreeResultsOrWithoutAMajorityConverted(int converted, int notWorthIt)
    {
        var results = Enumerable.Range(0, converted).Select(_ => Result(true, 45, "hevc", 3840, 2160, 50m))
            .Concat(Enumerable.Range(0, notWorthIt).Select(_ => Result(false, 45, "hevc", 3840, 2160)));

        Build([H264Rule()], [], [.. results]).Suggestions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("h264", 1080, 1440)]
    [InlineData("av1", 3840, 2160)]
    public void NoRuleSuggestionForAResolutionOrCodecNoRuleCanName(string codec, int width, int height)
    {
        var results = Enumerable.Range(0, 3).Select(_ => Result(true, 45, codec, width, height, 50m));

        Build([H264Rule()], [], [.. results]).Suggestions.Should().BeEmpty();
    }

    [Fact]
    public void TheThreeLargestNeverTriedGroupsEachNameAVideoNearTheirMedianBitrate()
    {
        var report = Build([H264Rule()], [
            Video("a-low.mp4", "h264", 1920, 1080, 10.5, 3 * GiB),
            Video("a-median.mp4", "h264", 1920, 1080, 12, 3 * GiB),
            Video("a-high.mp4", "h264", 1920, 1080, 14.5, 3 * GiB),
            Immich("b-immich.mp4", "hevc", 3840, 2160, 25, 4 * GiB),
            Video("b-file.mp4", "hevc", 3840, 2160, 28, 1 * GiB),
            Immich("c-1.mp4", null, 3840, 2160, 45, 5 * GiB),
            Immich("c-2.mp4", null, 3840, 2160, 50, 5 * GiB),
            Video("d-smallest.mp4", "h264", 1920, 1080, 7, 1 * GiB)]);

        report.Suggestions.Should().AllBeOfType<TestOneSuggestion>();
        report.Suggestions.Cast<TestOneSuggestion>().Select(test => (Describe(test.Group), test.Name, test.Path is null, test.ImmichAssetId)).Should().Equal(
            (" 4k+ 40-60 2", "c-1.mp4", true, "id-c-1.mp4"),
            ("h264 1080p-1440p 10-15 3", "a-median.mp4", false, null),
            ("hevc 4k+ 20-30 2", "b-file.mp4", false, null));
    }

    [Fact]
    public void NoTestSuggestionForAGroupThatWasTriedOrThatNoRuleCouldAdmit()
    {
        var report = Build([H264Rule()], [
            Video("tried.mp4", "h264", 1920, 1080, 12, 9 * GiB),
            Video("portrait.mp4", "h264", 1080, 1440, 8, 9 * GiB),
            Video("no-bitrate.mkv", "h264", 1920, 1080, null, 9 * GiB),
            Immich("no-size.mp4", null, null, null, 30, 9 * GiB)], [Result(false, 12)]);

        report.Suggestions.Should().BeEmpty();
    }

    [Fact]
    public void WithSuggestionsThereIsNoReasonForHavingNone()
    {
        var report = Build([H264Rule()], [Video("a.mp4", "hevc", 3840, 2160, 25)]);

        report.Suggestions.Should().NotBeEmpty();
        report.WhyNoSuggestions.Should().BeNull();
    }

    [Fact]
    public void NoVideosAnywhereIsItsOwnReasonEvenWithEarlierResults()
    {
        Build([H264Rule()], [], [Result(true, 20)]).WhyNoSuggestions.Should().Be(NoSuggestionsReason.NoVideos);
    }

    [Fact]
    public void VideosThatAreAllAlreadyAV1LeaveNothingToConvert()
    {
        var report = Build([H264Rule()], [Video("output.mkv", "av1", 3840, 2160, 10), Immich("replacement.mp4", null, 3840, 2160, 10) with { AlreadyConverted = true }], [Result(true, 20)]);

        report.WhyNoSuggestions.Should().Be(NoSuggestionsReason.AllAlreadyAv1);
    }

    [Fact]
    public void WithoutResultsAndEverythingAdmittedThereIsNothingKeptOutToTest()
    {
        var report = Build([H264Rule()], [Video("admitted.mp4", "h264", 1920, 1080, 20), Video("output.mkv", "av1", 3840, 2160, 10)]);

        report.WhyNoSuggestions.Should().Be(NoSuggestionsReason.NoResultsAllAdmitted);
    }

    [Fact]
    public void WithoutResultsAndOnlyGroupsNoRuleCouldAdmitThereIsNothingToTest()
    {
        var report = Build([H264Rule()], [Video("admitted.mp4", "h264", 1920, 1080, 20), Video("small.mp4", "h264", 1080, 1440, 8)]);

        report.WhyNoSuggestions.Should().Be(NoSuggestionsReason.NoResultsNothingTestable);
    }

    [Fact]
    public void ARuleWhoseResultsAllConvertedHasTooFewNotWorthConvertingResultsToRaiseAnything()
    {
        var report = Build([H264Rule()], [Video("admitted.mp4", "h264", 1920, 1080, 20)], [.. Enumerable.Range(0, 10).Select(index => Result(true, 20 + index))]);

        report.Rules[0].History!.Converted.Should().Be(10);
        report.WhyNoSuggestions.Should().Be(NoSuggestionsReason.TooFewResults);
    }

    [Fact]
    public void EnoughResultsThatSetNoBitrateApartNeedNoChange()
    {
        var report = Build([H264Rule()], [Video("admitted.mp4", "h264", 1920, 1080, 20)], [Result(false, 20), Result(false, 25), Result(false, 30), Result(true, 22)]);

        report.WhyNoSuggestions.Should().Be(NoSuggestionsReason.ResultsNeedNoChange);
    }

    [Fact]
    public void AKeptOutGroupWithMostlyNotWorthConvertingResultsNeedsNoChangeEither()
    {
        var results = Enumerable.Range(0, 3).Select(_ => Result(false, 45, "hevc", 3840, 2160));

        var report = Build([H264Rule()], [Video("admitted.mp4", "h264", 1920, 1080, 20)], [.. results]);

        report.WhyNoSuggestions.Should().Be(NoSuggestionsReason.ResultsNeedNoChange);
    }

    private static CalibrationReport Build(EligibilityRuleSettings[] rules, CalibrationVideo[] videos, JobRecord[]? jobs = null) =>
        CalibrationReport.Build(new AppSettings { Eligibility = new EligibilitySettings { Rules = [.. rules] }, Quality = new QualitySettings { MinimumVmaf = 97 } }, videos, jobs ?? [], Current);

    private static EligibilityRuleSettings H264Rule(string minimumBitrate = "15Mbps") =>
        new() { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = minimumBitrate, MinimumFileSize = "1MiB" };

    private static CalibrationVideo Video(string name, string codec, int? width, int? height, double? mbps, long size = 2 * GiB) =>
        new(name, $@"C:\videos\{name}", null, codec, width, height, Bitrate(mbps), size);

    private static CalibrationVideo Immich(string name, string? codec, int? width, int? height, double? mbps, long size = 2 * GiB) =>
        new(name, null, $"id-{name}", codec, width, height, Bitrate(mbps), size);

    private static JobRecord Result(bool converted, double mbps, string codec = "h264", int width = 1920, int height = 1080, decimal? saved = null, string? fingerprint = Current) => new()
    {
        Status = converted ? JobStatus.Completed : JobStatus.Skipped,
        QualityFingerprint = fingerprint,
        PercentageSaved = converted ? saved ?? 50m : null,
        VideoFacts = new VideoFacts(codec, width, height, Bitrate(mbps), 60, 2 * GiB)
    };

    private static JobRecord Change(JobRecord job, Action<JobRecord> change)
    {
        change(job);
        return job;
    }

    private static long? Bitrate(double? mbps) => mbps is { } value ? (long)(value * 1_000_000) : null;

    private static string Describe(CalibrationGroup group) =>
        $"{group.Codec} {group.Resolution.Label} {(group.Bitrate.IsKnown ? $"{group.Bitrate.MinimumMbps}-{group.Bitrate.MaximumMbps}" : "?")} {group.Videos}";
}
