using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Calibrate;

/// <summary>A whole-number range, for bitrates in Mbps and savings in percent.</summary>
public sealed record IntRange(int Min, int Max);

/// <summary>
/// One of the fixed resolution bands: the three rules can name, plus <c>&lt;1080p</c> below them. A null label means the
/// resolution is unknown.
/// </summary>
public sealed record ResolutionBand(string? Label)
{
    public static readonly ResolutionBand Unknown = new((string?)null);

    /// <summary>Only these can appear in a rule, so only these can be suggested as one or tested against one.</summary>
    public bool IsRuleBand => EligibilityEvaluator.IsValidResolutionBand(Label);

    // The same pixel-count edges as EligibilityEvaluator.ResolutionMatches, so a video's band and its rule never disagree.
    public static ResolutionBand For(int? width, int? height)
    {
        if (width is null || height is null) return Unknown;
        var pixels = (long)width.Value * height.Value;
        return new ResolutionBand(pixels switch
        {
            < 1_920L * 1_080 => "<1080p",
            < 2_560L * 1_440 => "1080p-1440p",
            < 3_840L * 2_160 => "1440p-4k",
            _ => "4k+"
        });
    }
}

/// <summary>
/// One of the fixed bitrate bands, in Mbps. Fixed rather than fitted to the library, so two runs group alike and a suggested
/// threshold lands on a round number. Both ends null means the bitrate is unknown; a null maximum is the open top band.
/// </summary>
public sealed record BitrateBand(int? MinimumMbps, int? MaximumMbps)
{
    public static readonly IReadOnlyList<int> Edges = [0, 5, 10, 15, 20, 30, 40, 60, 100];
    public static readonly BitrateBand Unknown = new(null, null);

    public bool IsKnown => MinimumMbps is not null;

    public static BitrateBand For(long? bitsPerSecond)
    {
        if (bitsPerSecond is not { } bitrate) return Unknown;
        for (var index = Edges.Count - 1; index >= 0; index--)
        {
            if (bitrate >= Edges[index] * 1_000_000L) return new BitrateBand(Edges[index], index + 1 < Edges.Count ? Edges[index + 1] : null);
        }

        return new BitrateBand(Edges[0], Edges[1]);
    }
}

/// <summary>
/// Finished results at the current quality settings: conversions with their saving, and videos the CRF search or the size
/// cap found not worth converting. Bitrates are each source's video stream bitrate.
/// </summary>
public sealed record CalibrationHistory(int Converted, IntRange? ConvertedBitrateMbps, IntRange? SavedPercent, int NotWorthIt, IntRange? NotWorthItBitrateMbps)
{
    public int Results => Converted + NotWorthIt;
}

public sealed record CalibrationRuleRow(int Number, EligibilityRuleSettings Rule, int Videos, long Bytes, CalibrationHistory? History);

/// <summary>Videos no rule admits, grouped by codec (null when unknown), resolution band and bitrate band.</summary>
public sealed record CalibrationGroup(string? Codec, ResolutionBand Resolution, BitrateBand Bitrate, int Videos, long Bytes, CalibrationHistory? History);

public abstract record CalibrationSuggestion;

/// <summary>
/// Every result below some bitrate in the rule was not worth converting, and every conversion sits above them: raising the
/// rule's minimum to <see cref="MinimumMbps"/> keeps out the first kind and admits the second.
/// </summary>
public sealed record RaiseMinimumBitrateSuggestion(int Rule, int MinimumMbps, int NotWorthIt, IntRange NotWorthItBitrateMbps, int Converted, int? ConvertedFromMbps) : CalibrationSuggestion;

/// <summary>Most results in a group no rule admits converted, so a rule for it would admit more like them.</summary>
public sealed record AddRuleSuggestion(CalibrationGroup Group, int MinimumMbps) : CalibrationSuggestion;

/// <summary>
/// A group with no results yet, and one typical video in it to test. <see cref="Path"/> is set for a file in a watch root;
/// otherwise the video is the Immich asset <see cref="ImmichAssetId"/>, which no command converts on its own.
/// </summary>
public sealed record TestOneSuggestion(CalibrationGroup Group, string Name, string? Path, string? ImmichAssetId) : CalibrationSuggestion;

/// <summary>
/// Why a report has no suggestions: one state per cause, so the line printed for it is true of that state alone. A report
/// with suggestions has none of these.
/// </summary>
public enum NoSuggestionsReason
{
    /// <summary>No video was found in the watch roots or Immich.</summary>
    NoVideos,

    /// <summary>Videos were found, and every one is already AV1.</summary>
    AllAlreadyAv1,

    /// <summary>No results, and the rules admit every video that is not AV1: nothing is kept out to test.</summary>
    NoResultsAllAdmitted,

    /// <summary>No results, and every group the rules keep out or leave codec-unknown is one no rule could name (under 1080p, or a resolution or bitrate unknown).</summary>
    NoResultsNothingTestable,

    /// <summary>Results exist, but no rule has 3 not-worth-converting results and no kept-out group has 3 results.</summary>
    TooFewResults,

    /// <summary>Enough results to judge, and they point to no change.</summary>
    ResultsNeedNoChange
}

/// <summary>
/// What the <c>calibrate</c> command reports: what each rule admits now, what the rules keep out, and what earlier runs at
/// the current quality settings found. Built from data alone so every grouping and suggestion rule is testable without
/// files, ffprobe or Immich.
/// </summary>
public sealed record CalibrationReport(
    double MinimumVmaf,
    IReadOnlyList<CalibrationRuleRow> Rules,
    IReadOnlyList<CalibrationGroup> KeptOut,
    IReadOnlyList<CalibrationGroup> CodecUnknown,
    int AlreadyAv1Videos,
    long AlreadyAv1Bytes,
    int OtherSettings,
    int NoVideoFacts,
    IReadOnlyList<CalibrationSuggestion> Suggestions)
{
    /// <summary>A suggestion resting on history needs at least this many results: fewer is an anecdote.</summary>
    public const int MinimumResults = 3;

    /// <summary>Never-tried groups named, largest first. More would bury the history-based suggestions.</summary>
    public const int MaximumTestSuggestions = 3;

    /// <summary>
    /// Why <see cref="Suggestions"/> is empty, or null when it is not. Derived from the report's own figures so that a report
    /// can never print a reason its numbers contradict. The checks run from the least to the most history, because each later
    /// state assumes the earlier ones do not hold.
    /// </summary>
    public NoSuggestionsReason? WhyNoSuggestions
    {
        get
        {
            if (Suggestions.Count > 0) return null;

            // AV1 videos sit outside every group, so what is left here are the videos a rule admits or keeps out.
            var convertible = Rules.Sum(row => row.Videos) + KeptOut.Sum(group => group.Videos) + CodecUnknown.Sum(group => group.Videos);
            if (convertible == 0) return AlreadyAv1Videos == 0 ? NoSuggestionsReason.NoVideos : NoSuggestionsReason.AllAlreadyAv1;

            // Nothing tried yet, so no group can have been tried, and any never-tried group a rule could name would have been
            // suggested: what is kept out or codec-unknown is all out of a rule's reach.
            if (Rules.All(row => row.History is null) && KeptOut.All(group => group.History is null))
                return KeptOut.Count == 0 && CodecUnknown.Count == 0 ? NoSuggestionsReason.NoResultsAllAdmitted : NoSuggestionsReason.NoResultsNothingTestable;

            // The same evidence the two history-based suggestions need: a rule's not-worth-converting results, and a kept-out
            // group's results of any kind.
            var enough = Rules.Any(row => row.History?.NotWorthIt >= MinimumResults) || KeptOut.Any(group => group.History?.Results >= MinimumResults);
            return enough ? NoSuggestionsReason.ResultsNeedNoChange : NoSuggestionsReason.TooFewResults;
        }
    }

    /// <summary>
    /// <paramref name="finishedJobs"/> are terminal jobs; only completed and skipped ones are results, and only those whose
    /// quality fingerprint is <paramref name="qualityFingerprint"/> count, since a result at another VMAF says nothing about
    /// this one. Each result is placed by checking its stored video facts against today's rules.
    /// </summary>
    public static CalibrationReport Build(AppSettings settings, IEnumerable<CalibrationVideo> videos, IEnumerable<JobRecord> finishedJobs, string qualityFingerprint)
    {
        var eligibility = settings.Eligibility;
        var ruleVideos = eligibility.Rules.Select(_ => new List<CalibrationVideo>()).ToArray();
        var ruleResults = eligibility.Rules.Select(_ => new List<Result>()).ToArray();
        var keptOutVideos = new Dictionary<GroupKey, List<CalibrationVideo>>();
        var keptOutResults = new Dictionary<GroupKey, List<Result>>();
        var unknownVideos = new Dictionary<GroupKey, List<CalibrationVideo>>();
        int alreadyAv1Videos = 0, otherSettings = 0, noVideoFacts = 0;
        long alreadyAv1Bytes = 0;

        foreach (var video in videos)
        {
            if (video.AlreadyConverted || (video.Codec is { } known && IsAv1(known) && Rule(video.SizeBytes, Media(video.Codec, video.Width, video.Height, video.Bitrate, video.SizeBytes), eligibility) is null))
            {
                alreadyAv1Videos++;
                alreadyAv1Bytes += video.SizeBytes;
                continue;
            }

            if (video.Codec is not { } codec)
            {
                Add(unknownVideos, new GroupKey(null, ResolutionBand.For(video.Width, video.Height), BitrateBand.For(video.Bitrate)), video);
                continue;
            }

            if (Rule(video.SizeBytes, Media(codec, video.Width, video.Height, video.Bitrate, video.SizeBytes), eligibility) is { } rule) ruleVideos[rule - 1].Add(video);
            else Add(keptOutVideos, Key(codec, video.Width, video.Height, video.Bitrate), video);
        }

        foreach (var job in finishedJobs)
        {
            if (job.Status is not (JobStatus.Completed or JobStatus.Skipped)) continue;
            if (!string.Equals(job.QualityFingerprint, qualityFingerprint, StringComparison.Ordinal)) { otherSettings++; continue; }
            if (job.VideoFacts is not { } facts) { noVideoFacts++; continue; }

            var result = new Result(job.Status == JobStatus.Completed, facts.Bitrate, job.PercentageSaved);
            if (Rule(facts.SizeBytes, Media(facts.Codec, facts.Width, facts.Height, facts.Bitrate, facts.SizeBytes), eligibility) is { } rule) ruleResults[rule - 1].Add(result);
            else Add(keptOutResults, Key(facts.Codec, facts.Width, facts.Height, facts.Bitrate), result);
        }

        var rules = eligibility.Rules
            .Select((rule, index) => new CalibrationRuleRow(index + 1, rule, ruleVideos[index].Count, ruleVideos[index].Sum(video => video.SizeBytes), History(ruleResults[index])))
            .ToArray();
        var keptOut = keptOutVideos.Keys.Union(keptOutResults.Keys)
            .Select(key => Group(key, keptOutVideos.GetValueOrDefault(key), History(keptOutResults.GetValueOrDefault(key) ?? [])))
            .OrderByDescending(group => group.Bytes).ThenByDescending(group => group.History?.Results ?? 0).ThenBy(Label, StringComparer.Ordinal)
            .ToArray();
        var codecUnknown = unknownVideos
            .Select(pair => Group(pair.Key, pair.Value, null))
            .OrderByDescending(group => group.Bytes).ThenBy(Label, StringComparer.Ordinal)
            .ToArray();

        var suggestions = new List<CalibrationSuggestion>();
        for (var index = 0; index < rules.Length; index++)
        {
            if (RaiseMinimumBitrate(index + 1, ruleResults[index]) is { } raise) suggestions.Add(raise);
        }

        foreach (var group in keptOut)
        {
            if (group.History is { } history && history.Results >= MinimumResults && history.Converted * 2 > history.Results &&
                group.Resolution.IsRuleBand && group.Bitrate.MinimumMbps is > 0 and { } minimum)
                suggestions.Add(new AddRuleSuggestion(group, minimum));
        }

        // Only groups a rule could admit: a rule cannot name <1080p or an unknown bitrate, so testing one would change nothing.
        var neverTried = keptOut.Where(group => group.History is null && group.Videos > 0 && !IsAv1(group.Codec!))
            .Select(group => (Group: group, Videos: keptOutVideos[new GroupKey(group.Codec, group.Resolution, group.Bitrate)]))
            .Concat(codecUnknown.Select(group => (Group: group, Videos: unknownVideos[new GroupKey(null, group.Resolution, group.Bitrate)])))
            .Where(candidate => candidate.Group.Resolution.IsRuleBand && candidate.Group.Bitrate.IsKnown)
            .OrderByDescending(candidate => candidate.Group.Bytes).ThenBy(candidate => Label(candidate.Group), StringComparer.Ordinal)
            .Take(MaximumTestSuggestions);
        foreach (var (group, members) in neverTried)
        {
            var typical = Typical(members);
            suggestions.Add(new TestOneSuggestion(group, typical.Name, typical.Path, typical.Path is null ? typical.ImmichAssetId : null));
        }

        return new CalibrationReport(settings.Quality.MinimumVmaf, rules, keptOut, codecUnknown, alreadyAv1Videos, alreadyAv1Bytes, otherSettings, noVideoFacts, suggestions);
    }

    /// <summary>A sort key and tie-breaker; the CLI writes its own label.</summary>
    private static string Label(CalibrationGroup group) => $"{group.Codec} {group.Resolution.Label} {group.Bitrate.MinimumMbps:D3}";

    /// <summary>
    /// Raises a rule's minimum bitrate when its "not worth converting" results all sit below its conversions. The new minimum is the
    /// first band edge above the highest "not worth converting" result, or that result's next whole Mbps when no edge fits below the
    /// lowest conversion; with neither, the results are too close to split.
    /// </summary>
    private static RaiseMinimumBitrateSuggestion? RaiseMinimumBitrate(int rule, List<Result> results)
    {
        var notWorthIt = results.Where(result => !result.Converted && result.Bitrate is not null).Select(result => result.Bitrate!.Value).ToArray();
        var converted = results.Where(result => result.Converted && result.Bitrate is not null).Select(result => result.Bitrate!.Value).ToArray();
        if (notWorthIt.Length < MinimumResults) return null;
        var highestNotWorthIt = notWorthIt.Max();
        long? lowestConverted = converted.Length == 0 ? null : converted.Min();

        // The new minimum is above every "not worth converting" result, so requiring it to admit every conversion also rejects a
        // conversion at or below one of them: no bitrate separates the two outcomes then.
        bool Admits(long minimumBitsPerSecond) => lowestConverted is null || minimumBitsPerSecond <= lowestConverted;
        var edge = BitrateBand.Edges.Select(mbps => (int?)mbps).FirstOrDefault(mbps => mbps * 1_000_000L > highestNotWorthIt);
        var minimum = edge is { } byEdge && Admits(byEdge * 1_000_000L) ? byEdge : (int)(highestNotWorthIt / 1_000_000) + 1;
        if (!Admits(minimum * 1_000_000L)) return null;
        return new RaiseMinimumBitrateSuggestion(rule, minimum, notWorthIt.Length, Range(notWorthIt.Select(Mbps))!, converted.Length, lowestConverted is { } lowest ? Mbps(lowest) : null);
    }

    /// <summary>
    /// The video to test from a group: the one nearest its median bitrate, so it stands for the group. A watch-root file is
    /// preferred because it can be tested with one command; an Immich asset can only be named.
    /// </summary>
    private static CalibrationVideo Typical(List<CalibrationVideo> members)
    {
        var bitrates = members.Select(video => video.Bitrate ?? 0).Order().ToArray();
        var median = bitrates[(bitrates.Length - 1) / 2];
        var files = members.Where(video => video.Path is not null).ToArray();
        return (files.Length > 0 ? files : members.ToArray())
            .OrderBy(video => Math.Abs((video.Bitrate ?? 0) - median)).ThenBy(video => video.Name, StringComparer.Ordinal)
            .First();
    }

    private static CalibrationHistory? History(List<Result> results)
    {
        if (results.Count == 0) return null;
        var converted = results.Where(result => result.Converted).ToArray();
        var notWorthIt = results.Where(result => !result.Converted).ToArray();
        return new CalibrationHistory(
            converted.Length,
            Range(converted.Where(result => result.Bitrate is not null).Select(result => Mbps(result.Bitrate!.Value))),
            Range(converted.Where(result => result.PercentSaved is not null).Select(result => (int)Math.Round(result.PercentSaved!.Value, MidpointRounding.AwayFromZero))),
            notWorthIt.Length,
            Range(notWorthIt.Where(result => result.Bitrate is not null).Select(result => Mbps(result.Bitrate!.Value))));
    }

    private static IntRange? Range(IEnumerable<int> values)
    {
        var all = values.ToArray();
        return all.Length == 0 ? null : new IntRange(all.Min(), all.Max());
    }

    private static int Mbps(long bitsPerSecond) => (int)Math.Round(bitsPerSecond / 1_000_000d, MidpointRounding.AwayFromZero);

    private static CalibrationGroup Group(GroupKey key, List<CalibrationVideo>? videos, CalibrationHistory? history) =>
        new(key.Codec, key.Resolution, key.Bitrate, videos?.Count ?? 0, videos?.Sum(video => video.SizeBytes) ?? 0, history);

    private static GroupKey Key(string codec, int? width, int? height, long? bitrate) =>
        new(codec.ToLowerInvariant(), ResolutionBand.For(width, height), BitrateBand.For(bitrate));

    private static int? Rule(long sizeBytes, MediaInfo media, EligibilitySettings eligibility) => EligibilityEvaluator.MatchingRule(sizeBytes, media, eligibility);

    private static MediaInfo Media(string codec, int? width, int? height, long? bitrate, long sizeBytes) =>
        new(codec, VideoStreamCount: 1, AudioStreamCount: 0, SubtitleStreamCount: 0, AttachmentCount: 0, DurationSeconds: null, sizeBytes, width, height, bitrate);

    private static bool IsAv1(string codec) => string.Equals(codec, "av1", StringComparison.OrdinalIgnoreCase);

    private static void Add<T>(Dictionary<GroupKey, List<T>> groups, GroupKey key, T item)
    {
        if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
        list.Add(item);
    }

    private sealed record GroupKey(string? Codec, ResolutionBand Resolution, BitrateBand Bitrate);

    private sealed record Result(bool Converted, long? Bitrate, decimal? PercentSaved);
}
