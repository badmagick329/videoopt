using System.Globalization;
using VideoOptimiser.Application.Features.Calibrate;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli.Features.Calibrate;

/// <summary>
/// The Immich half of <c>calibrate</c>'s progress, paced like <see cref="QueueDiscoverConsole"/>: silent for a search that
/// ends quickly, then a count at most every 10 seconds.
/// </summary>
internal sealed class ImmichSearchConsole(TextWriter output, Func<DateTimeOffset>? utcNow = null) : IProgress<int>
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private DateTimeOffset? _lastLineAt;

    public bool WroteLines { get; private set; }

    public void Report(int searched)
    {
        var now = _utcNow();
        _lastLineAt ??= now;
        if (now - _lastLineAt < QueueDiscoverConsole.ProgressInterval) return;
        _lastLineAt = now;
        output.WriteLine($"Searched {searched} Immich videos so far...");
        WroteLines = true;
    }
}

/// <summary>
/// The <c>calibrate</c> report as text and as JSON. Both carry the same sentences for suggestions, so an agent reading the
/// JSON can quote what the user saw.
/// </summary>
internal static class CalibrateConsole
{
    /// <summary>Kept-out and codec-unknown groups shown, largest first; <c>--json</c> lists the rest.</summary>
    internal const int ShownGroups = 10;

    public static void Write(TextWriter output, CalibrationReport report)
    {
        output.WriteLine($"Admitted by your rules (quality target VMAF {report.MinimumVmaf.ToString("0.##", CultureInfo.InvariantCulture)})");
        WriteRows(output, report.Rules.Select(row => new Row($"Rule {row.Number}  {RuleLabel(row)}", row.Videos, row.Bytes, row.History)).ToArray());

        if (report.KeptOut.Count > 0)
        {
            output.WriteLine("Kept out by your rules");
            WriteGroups(output, report.KeptOut);
        }

        if (report.CodecUnknown.Count > 0)
        {
            output.WriteLine("Codec unknown until downloaded (Immich; bitrate includes audio)");
            // No history column: every result has a codec, so it is never one of these groups.
            WriteGroups(output, report.CodecUnknown, withHistory: false);
        }

        if (report.AlreadyAv1Videos > 0) output.WriteLine($"Already AV1: {Videos(report.AlreadyAv1Videos)}, {HumanReadableValues.FormatSize(report.AlreadyAv1Bytes)}.");
        var leftOut = new List<string>();
        if (report.OtherSettings > 0) leftOut.Add($"{OutputWords.Count(report.OtherSettings, "result", "results")} from other or unrecorded quality settings");
        if (report.NoVideoFacts > 0) leftOut.Add($"{OutputWords.Count(report.NoVideoFacts, "job", "jobs")} without video facts");
        if (leftOut.Count > 0) output.WriteLine($"Left out of \"earlier\": {string.Join(", ", leftOut)}.");

        output.WriteLine();
        if (report.WhyNoSuggestions is { } reason)
        {
            output.WriteLine(Text(reason));
            return;
        }

        output.WriteLine("Suggestions");
        foreach (var suggestion in report.Suggestions) output.WriteLine($"  - {Text(suggestion)}");
    }

    public static CalibrationJsonOutput ToJson(CalibrationReport report) => new(
        report.MinimumVmaf,
        report.Rules.Select(row => new CalibrationRuleJson(row.Number, [.. row.Rule.Codecs], row.Rule.Resolution, row.Rule.MinimumVideoBitrate, row.Rule.MinimumFileSize, row.Videos, row.Bytes, HistoryJson(row.History))).ToArray(),
        report.KeptOut.Select(group => new CalibrationGroupJson(group.Codec!, group.Resolution.Label, group.Bitrate.MinimumMbps, group.Bitrate.MaximumMbps, group.Videos, group.Bytes, HistoryJson(group.History))).ToArray(),
        report.CodecUnknown.Select(group => new CalibrationUnknownJson(group.Resolution.Label, group.Bitrate.MinimumMbps, group.Bitrate.MaximumMbps, group.Videos, group.Bytes)).ToArray(),
        new CalibrationTotalJson(report.AlreadyAv1Videos, report.AlreadyAv1Bytes),
        new CalibrationLeftOutJson(report.OtherSettings, report.NoVideoFacts),
        report.Suggestions.Select(SuggestionJson).ToArray(),
        report.WhyNoSuggestions is { } reason ? new CalibrationNoSuggestionsJson(Name(reason), Text(reason)) : null);

    // One line per state, each true of that state alone: a rule whose results all converted is not "too few results", and
    // a library of AV1 is not "no results". The JSON names the state so an agent need not parse the sentence.
    internal static string Text(NoSuggestionsReason reason) => reason switch
    {
        NoSuggestionsReason.NoVideos => "No suggestions: no videos were found.",
        NoSuggestionsReason.AllAlreadyAv1 => "No suggestions: every video found is already AV1.",
        NoSuggestionsReason.NoResultsAllAdmitted =>
            "No suggestions yet: no video has been converted at these quality settings, and your rules already take every video that isn't AV1.",
        NoSuggestionsReason.NoResultsNothingTestable =>
            "No suggestions yet: no video has been converted at these quality settings, and the videos your rules leave out can't be tested (each is under 1080p or has no known resolution or bitrate).",
        NoSuggestionsReason.TooFewResults =>
            $"No suggestions: a change needs {CalibrationReport.MinimumResults} or more results in one group, and no group has that many at these quality settings. No untried group is left to test.",
        NoSuggestionsReason.ResultsNeedNoChange =>
            "No suggestions: the results at these quality settings don't call for a change to your rules, and no untried group is left to test.",
        _ => throw new InvalidOperationException($"Unknown reason {reason}.")
    };

    private static string Name(NoSuggestionsReason reason) => reason switch
    {
        NoSuggestionsReason.NoVideos => "noVideos",
        NoSuggestionsReason.AllAlreadyAv1 => "allAlreadyAv1",
        NoSuggestionsReason.NoResultsAllAdmitted => "noResultsAllAdmitted",
        NoSuggestionsReason.NoResultsNothingTestable => "noResultsNothingTestable",
        NoSuggestionsReason.TooFewResults => "tooFewResults",
        NoSuggestionsReason.ResultsNeedNoChange => "resultsNeedNoChange",
        _ => throw new InvalidOperationException($"Unknown reason {reason}.")
    };

    private static CalibrationSuggestionJson SuggestionJson(CalibrationSuggestion suggestion) => suggestion switch
    {
        RaiseMinimumBitrateSuggestion raise => new RaiseMinimumBitrateJson(raise.Rule, Mbps(raise.MinimumMbps), Text(raise)),
        AddRuleSuggestion add => new AddRuleJson(add.Group.Codec!, add.Group.Resolution.Label!, Mbps(add.MinimumMbps), Text(add)),
        TestOneSuggestion test => new TestOneJson(test.Group.Codec, test.Group.Resolution.Label, test.Group.Bitrate.MinimumMbps, test.Group.Bitrate.MaximumMbps, test.Path, test.ImmichAssetId, test.Path is null ? null : TestCommand(test.Path), Text(test)),
        _ => throw new InvalidOperationException($"Unknown suggestion {suggestion.GetType().Name}.")
    };

    internal static string Text(CalibrationSuggestion suggestion) => suggestion switch
    {
        RaiseMinimumBitrateSuggestion raise =>
            $"Rule {raise.Rule}: all {raise.NotWorthIt} results {(raise.NotWorthItBitrateMbps.Min == raise.NotWorthItBitrateMbps.Max ? $"at {raise.NotWorthItBitrateMbps.Min}" : $"from {raise.NotWorthItBitrateMbps.Min} to {raise.NotWorthItBitrateMbps.Max}")} Mbps were not worth converting" +
            (raise.ConvertedFromMbps is { } from ? $"; the {raise.Converted} from {from} Mbps up converted" : string.Empty) +
            $". Consider minimumVideoBitrate: \"{Mbps(raise.MinimumMbps)}\".",
        AddRuleSuggestion add =>
            $"{GroupLabel(add.Group)}: {add.Group.History!.Converted} of {add.Group.History.Results} earlier results converted" +
            (add.Group.History.SavedPercent is { } saved ? $", saving {Range(saved)}%" : string.Empty) +
            $". Consider a rule: codecs [\"{add.Group.Codec}\"], resolution \"{add.Group.Resolution.Label}\", minimumVideoBitrate \"{Mbps(add.MinimumMbps)}\".",
        TestOneSuggestion { Path: { } path } test =>
            $"Never tried: {GroupLabel(test.Group)} ({Videos(test.Group.Videos)}, {HumanReadableValues.FormatSize(test.Group.Bytes)}). Test one: {TestCommand(path)}",
        TestOneSuggestion test =>
            $"Never tried in Immich: {GroupLabel(test.Group)} ({Videos(test.Group.Videos)}, {HumanReadableValues.FormatSize(test.Group.Bytes)}). A typical one: {test.Name}",
        _ => throw new InvalidOperationException($"Unknown suggestion {suggestion.GetType().Name}.")
    };

    // --force because the group is one the rules keep out; process stops before replacing anything, so this only tests.
    private static string TestCommand(string path) => $"video-optimiser queue add \"{path}\" --force";

    private static void WriteGroups(TextWriter output, IReadOnlyList<CalibrationGroup> groups, bool withHistory = true)
    {
        WriteRows(output, groups.Take(ShownGroups).Select(group => new Row(GroupLabel(group), group.Videos, group.Bytes, group.History)).ToArray(), withHistory);
        var hidden = groups.Skip(ShownGroups).ToArray();
        if (hidden.Length > 0)
            output.WriteLine($"  and {OutputWords.Count(hidden.Length, "more group", "more groups")} ({HumanReadableValues.FormatSize(hidden.Sum(group => group.Bytes))}). --json lists them all.");
    }

    /// <summary>One aligned block: label, video count and size right-aligned, then the history.</summary>
    private static void WriteRows(TextWriter output, Row[] rows, bool withHistory = true)
    {
        if (rows.Length == 0) return;
        var labelWidth = rows.Max(row => row.Label.Length);
        var countWidth = rows.Max(row => Videos(row.Videos).Length);
        var sizeWidth = rows.Max(row => SizeText(row).Length);
        foreach (var row in rows)
        {
            var history = withHistory ? HistoryText(row.History, row.Videos) : string.Empty;
            output.WriteLine($"  {row.Label.PadRight(labelWidth)}  {Videos(row.Videos).PadLeft(countWidth)}  {SizeText(row).PadLeft(sizeWidth)}  {history}".TrimEnd());
        }
    }

    private static string SizeText(Row row) => row.Videos == 0 ? string.Empty : HumanReadableValues.FormatSize(row.Bytes);

    /// <summary>
    /// <c>earlier: …</c> with the larger outcome first, so the line leads with what mostly happened; a tie leads with
    /// conversions. Nothing at all for an empty row with no history, since "never tried" would be noise there.
    /// </summary>
    private static string HistoryText(CalibrationHistory? history, int videos)
    {
        if (history is null) return videos == 0 ? string.Empty : "never tried";
        var converted = history.Converted == 0 ? null :
            $"{history.Converted} converted{(history.ConvertedBitrateMbps is { } bitrate ? $" at {Range(bitrate)} Mbps" : string.Empty)}{(history.SavedPercent is { } saved ? $", saved {Range(saved)}%" : string.Empty)}";
        var notWorthConverting = history.NotWorthIt == 0 ? null :
            $"{history.NotWorthIt} not worth converting{(history.NotWorthItBitrateMbps is { } bitrate2 ? $" at {Range(bitrate2)} Mbps" : string.Empty)}";
        var parts = history.NotWorthIt > history.Converted ? new[] { notWorthConverting, converted } : new[] { converted, notWorthConverting };
        return $"earlier: {string.Join("; ", parts.OfType<string>())}";
    }

    private static string RuleLabel(CalibrationRuleRow row) =>
        $"{string.Join("/", row.Rule.Codecs)} {row.Rule.Resolution} >={row.Rule.MinimumVideoBitrate} >={row.Rule.MinimumFileSize}";

    internal static string GroupLabel(CalibrationGroup group) =>
        string.Join(' ', new[] { group.Codec, group.Resolution.Label ?? "resolution unknown", BandLabel(group.Bitrate) }.OfType<string>());

    private static string BandLabel(BitrateBand band) => band switch
    {
        { MinimumMbps: null } => "bitrate unknown",
        { MaximumMbps: null } => $"{band.MinimumMbps}+ Mbps",
        _ => $"{band.MinimumMbps}-{band.MaximumMbps} Mbps"
    };

    private static string Range(IntRange range) => range.Min == range.Max ? $"{range.Min}" : $"{range.Min}-{range.Max}";

    private static string Mbps(int value) => $"{value}Mbps";

    private static string Videos(int count) => OutputWords.Count(count, "video", "videos");

    private static CalibrationHistoryJson? HistoryJson(CalibrationHistory? history) => history is null ? null : new(
        history.Converted, RangeJson(history.ConvertedBitrateMbps), RangeJson(history.SavedPercent), history.NotWorthIt, RangeJson(history.NotWorthItBitrateMbps));

    private static CalibrationRangeJson? RangeJson(IntRange? range) => range is null ? null : new(range.Min, range.Max);

    private sealed record Row(string Label, int Videos, long Bytes, CalibrationHistory? History);
}
