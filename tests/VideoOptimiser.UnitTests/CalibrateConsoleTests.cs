using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using VideoOptimiser.Application.Calibration;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Cli;

namespace VideoOptimiser.UnitTests;

/// <summary>The approved wording of <c>calibrate</c>, line for line, and the agreed <c>--json</c> shape.</summary>
public sealed class CalibrateConsoleTests
{
    private const long GiB = 1L << 30;

    private static readonly CalibrationGroup NeverTried = new("h264", new ResolutionBand("1080p-1440p"), new BitrateBand(10, 15), 340, 10 * GiB, null);
    private static readonly CalibrationGroup Converts = new("hevc", new ResolutionBand("4k+"), new BitrateBand(40, 60), 12, 5 * GiB,
        new CalibrationHistory(4, new IntRange(44, 58), new IntRange(41, 55), 1, new IntRange(41, 41)));
    private static readonly CalibrationGroup Unknown = new(null, new ResolutionBand("1080p-1440p"), new BitrateBand(15, 20), 220, 3 * GiB, null);

    [Fact]
    public void TheReportPrintsEachSectionInTheApprovedWording()
    {
        var report = new CalibrationReport(97,
            [
                new CalibrationRuleRow(1, Rule("h264", "4k+", "35Mbps"), 140, 60 * GiB, new CalibrationHistory(23, new IntRange(77, 83), new IntRange(70, 89), 0, null)),
                new CalibrationRuleRow(2, Rule("h264", "1080p-1440p", "15Mbps"), 0, 0, new CalibrationHistory(2, new IntRange(40, 45), new IntRange(71, 80), 5, new IntRange(20, 25))),
                new CalibrationRuleRow(3, Rule("hevc", "4k+", "30Mbps"), 0, 0, null)
            ],
            [NeverTried, Converts], [Unknown], 58, 120 * GiB, 99, 1,
            [
                new RaiseMinimumBitrateSuggestion(2, 30, 5, new IntRange(20, 25), 2, 40),
                new AddRuleSuggestion(Converts, 40),
                new TestOneSuggestion(NeverTried, "clip.mp4", @"D:\Videos\clip.mp4", null),
                new TestOneSuggestion(Unknown, "PXL_20240512_101500.mp4", null, "asset-1")
            ]);

        Lines(report).Should().Equal(
            "Admitted by your rules (quality target VMAF 97)",
            "  Rule 1  h264 4k+ >=35Mbps >=300MiB          140 videos  60.0 GiB  earlier: 23 converted at 77-83 Mbps, saved 70-89%",
            "  Rule 2  h264 1080p-1440p >=15Mbps >=300MiB    0 videos            earlier: 5 not worth it at 20-25 Mbps; 2 converted at 40-45 Mbps, saved 71-80%",
            "  Rule 3  hevc 4k+ >=30Mbps >=300MiB            0 videos",
            "Kept out by your rules",
            "  h264 1080p-1440p 10-15 Mbps  340 videos  10.0 GiB  never tried",
            "  hevc 4k+ 40-60 Mbps           12 videos   5.0 GiB  earlier: 4 converted at 44-58 Mbps, saved 41-55%; 1 not worth it at 41 Mbps",
            "Codec unknown until downloaded (Immich; bitrate includes audio)",
            "  1080p-1440p 15-20 Mbps  220 videos  3.0 GiB",
            "Already AV1: 58 videos, 120.0 GiB.",
            "Left out of \"earlier\": 99 results from other or unrecorded quality settings, 1 job without video facts.",
            "",
            "Suggestions",
            "  - Rule 2: all 5 results from 20 to 25 Mbps were not worth it; the 2 from 40 Mbps up converted. Consider minimumVideoBitrate: \"30Mbps\".",
            "  - hevc 4k+ 40-60 Mbps: 4 of 5 earlier results converted, saving 41-55%. Consider a rule: codecs [\"hevc\"], resolution \"4k+\", minimumVideoBitrate \"40Mbps\".",
            "  - Never tried: h264 1080p-1440p 10-15 Mbps (340 videos, 10.0 GiB). Test one: video-optimiser queue add \"D:\\Videos\\clip.mp4\" --force",
            "  - Never tried in Immich: 1080p-1440p 15-20 Mbps (220 videos, 3.0 GiB). A typical one: PXL_20240512_101500.mp4");
    }

    [Fact]
    public void AReportWithNothingToSuggestSaysSoAndLeavesOutEmptySections()
    {
        var report = new CalibrationReport(95.5, [new CalibrationRuleRow(1, Rule("h264", "4k+", "35Mbps"), 3, 6 * GiB, null)], [], [], 0, 0, 0, 0, []);

        Lines(report).Should().Equal(
            "Admitted by your rules (quality target VMAF 95.5)",
            "  Rule 1  h264 4k+ >=35Mbps >=300MiB  3 videos  6.0 GiB  never tried",
            "",
            "No suggestions: no group has 3 or more results at these settings yet.");
    }

    [Fact]
    public void GroupsBeyondTheFirstTenAreSummedIntoOneLine()
    {
        var groups = Enumerable.Range(0, 12).Select(index => NeverTried with { Bitrate = new BitrateBand(index, index + 1), Bytes = (12 - index) * GiB, Videos = 1 }).ToArray();
        var report = new CalibrationReport(97, [], groups, [], 0, 0, 0, 0, []);

        var lines = Lines(report);

        lines.Count(line => line.StartsWith("  h264", StringComparison.Ordinal)).Should().Be(10);
        lines.Should().Contain("  and 2 more groups (3.0 GiB). --json lists them all.");
    }

    [Fact]
    public void UnknownAndOpenEndedBandsHaveTheirOwnLabels()
    {
        CalibrateConsole.GroupLabel(NeverTried with { Bitrate = BitrateBand.Unknown }).Should().Be("h264 1080p-1440p bitrate unknown");
        CalibrateConsole.GroupLabel(NeverTried with { Bitrate = new BitrateBand(100, null) }).Should().Be("h264 1080p-1440p 100+ Mbps");
        CalibrateConsole.GroupLabel(Unknown with { Resolution = ResolutionBand.Unknown }).Should().Be("resolution unknown 15-20 Mbps");
    }

    [Fact]
    public void TheJsonCarriesTheAgreedFieldsWithEachSuggestionKindsOwn()
    {
        var report = new CalibrationReport(97,
            [new CalibrationRuleRow(2, Rule("h264", "1080p-1440p", "15Mbps"), 118, 60 * GiB, new CalibrationHistory(2, new IntRange(40, 45), new IntRange(71, 80), 5, new IntRange(20, 25)))],
            [NeverTried with { Bitrate = new BitrateBand(100, null) }], [Unknown], 58, 120 * GiB, 99, 12,
            [
                new RaiseMinimumBitrateSuggestion(2, 30, 5, new IntRange(20, 25), 2, 40),
                new AddRuleSuggestion(Converts, 40),
                new TestOneSuggestion(Unknown, "PXL_1.mp4", null, "asset-1")
            ]);

        var json = JsonNode.Parse(JsonSerializer.Serialize(CalibrateConsole.ToJson(report), CliJsonContext.Default.CalibrationJsonOutput))!;

        json.AsObject().Select(property => property.Key).Should().Equal("minimumVmaf", "rules", "keptOut", "codecUnknown", "alreadyAv1", "historyLeftOut", "suggestions");
        json["rules"]![0]!.ToJsonString().Should().Be(
            """{"number":2,"codecs":["h264"],"resolution":"1080p-1440p","minimumVideoBitrate":"15Mbps","minimumFileSize":"300MiB","videos":118,"bytes":64424509440,"history":{"converted":2,"convertedBitrateMbps":{"min":40,"max":45},"savedPercent":{"min":71,"max":80},"notWorthIt":5,"notWorthItBitrateMbps":{"min":20,"max":25}}}""");
        json["keptOut"]![0]!.ToJsonString().Should().Be(
            """{"codec":"h264","resolution":"1080p-1440p","minimumMbps":100,"maximumMbps":null,"videos":340,"bytes":10737418240,"history":null}""");
        json["codecUnknown"]![0]!.ToJsonString().Should().Be("""{"resolution":"1080p-1440p","minimumMbps":15,"maximumMbps":20,"videos":220,"bytes":3221225472}""");
        json["alreadyAv1"]!.ToJsonString().Should().Be("""{"videos":58,"bytes":128849018880}""");
        json["historyLeftOut"]!.ToJsonString().Should().Be("""{"otherSettings":99,"noVideoFacts":12}""");
        var suggestions = json["suggestions"]!.AsArray();
        suggestions.Select(suggestion => string.Join(",", suggestion!.AsObject().Select(property => property.Key))).Should().Equal(
            "kind,rule,minimumVideoBitrate,text",
            "kind,codec,resolution,minimumVideoBitrate,text",
            "kind,codec,resolution,minimumMbps,maximumMbps,path,immichAssetId,command,text");
        suggestions.Select(suggestion => suggestion!["kind"]!.GetValue<string>()).Should().Equal("raiseMinimumBitrate", "addRule", "testOne");
        suggestions[0]!["minimumVideoBitrate"]!.GetValue<string>().Should().Be("30Mbps");
        suggestions[1]!["minimumVideoBitrate"]!.GetValue<string>().Should().Be("40Mbps");
        suggestions[2]!["command"].Should().BeNull("no command converts one Immich asset");
        suggestions[2]!["text"]!.GetValue<string>().Should().Be(CalibrateConsole.Text(report.Suggestions[2]));
    }

    private static EligibilityRuleSettings Rule(string codec, string resolution, string bitrate) =>
        new() { Codecs = [codec], Resolution = resolution, MinimumVideoBitrate = bitrate, MinimumFileSize = "300MiB" };

    private static string[] Lines(CalibrationReport report)
    {
        var output = new StringWriter();
        CalibrateConsole.Write(output, report);
        return output.ToString().Split(Environment.NewLine)[..^1];
    }
}
