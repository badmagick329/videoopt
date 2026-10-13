using System.Globalization;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli;

/// <summary>
/// Prints Immich discovery as it happens. A user once saw 15 downloads scroll past and concluded the tool downloads every
/// video, when each rejected download was deleted at once; so the output opens by saying what discovery does, shows one line
/// per downloaded candidate and ends with what was found. A candidate's line is written in two parts, the file and date
/// before its download starts and the rest when known, so a slow download shows which file it is on. The rest names the
/// candidate's codec, resolution, bitrate and size before the verdict, because a verdict such as "rule 1 needs 2GiB" is
/// only readable beside the facts it was reached from: one user could not see why a 319 MiB video was queued while two
/// others were skipped for being under 2 GiB, when the difference was the codec.
/// </summary>
internal sealed class ImmichDiscoveryConsole(TextWriter output, TextWriter error) : IProgress<ImmichDiscoveryProgress>
{
    private readonly object _gate = new();
    private int? _limit;
    private int _ruleCount;
    // The open candidate's line so far; set while its download runs, null once its outcome is out.
    private string? _prefix;
    private bool _lineOpen;

    public void Report(ImmichDiscoveryProgress value)
    {
        lock (_gate)
        {
            // Anything that is not the outcome of the open candidate must not land in the middle of its line: end the line
            // there, and give the outcome the whole line again when it arrives.
            if (value is not ImmichDiscoveryProgress.CandidateJudged && _lineOpen)
            {
                output.WriteLine();
                _lineOpen = false;
            }

            switch (value)
            {
                case ImmichDiscoveryProgress.Started started:
                    _limit = started.Limit;
                    _ruleCount = started.RuleCount;
                    output.WriteLine(started.Limit is { } limit
                        ? $"Searching Immich for {OutputWords.Count(limit, "video", "videos")} to convert. Each candidate is downloaded and checked with"
                        : "Searching Immich for every video that fits your rules. Each candidate is downloaded and checked with");
                    output.WriteLine("ffprobe; ones that don't fit your rules are deleted and skipped in future runs.");
                    if (started.AlreadyQueued > 0)
                        output.WriteLine($"{started.AlreadyQueued} already queued from earlier runs.");
                    if (started.StaleSkips > 0)
                        output.WriteLine($"Your eligibility rules changed since the last run, so {OutputWords.Count(started.StaleSkips, "video", "videos")} skipped before will be downloaded and checked again.");
                    output.WriteLine();
                    break;
                case ImmichDiscoveryProgress.Restaging restaging:
                    output.WriteLine($"Downloading again: {restaging.FileName} (job {restaging.JobId:N} is open but its staged copy is missing)");
                    break;
                case ImmichDiscoveryProgress.Restaged restaged:
                    output.WriteLine(restaged.Problem is null ? "  done: the staged copy is back" : $"  issue: {restaged.Problem}");
                    break;
                case ImmichDiscoveryProgress.CandidateDownloading candidate:
                    _prefix = $"[{candidate.Number}] {candidate.FileName}  {candidate.Taken.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}  ";
                    output.Write(_prefix);
                    _lineOpen = true;
                    break;
                case ImmichDiscoveryProgress.CandidateJudged judged:
                    // A judgement without a candidate cannot come from the service; ignoring it keeps the layout whole.
                    if (_prefix is null) break;
                    output.WriteLine((_lineOpen ? string.Empty : _prefix) + Outcome(judged));
                    _lineOpen = false;
                    _prefix = null;
                    break;
                case ImmichDiscoveryProgress.AssetExcluded excluded:
                    error.WriteLine($"Excluded: {excluded.FileName} (asset {excluded.AssetId}) {excluded.Reason}; left alone.");
                    break;
                case ImmichDiscoveryProgress.AssetIssue issue:
                    output.WriteLine($"Issue: {issue.FileName}: {issue.Message}.");
                    break;
            }
        }
    }

    /// <summary>
    /// Ends a candidate line the run was interrupted in, by Ctrl+C or an error that stops discovery, so the message that
    /// follows starts on its own line and the line says why it has no outcome.
    /// </summary>
    public void FinishLine()
    {
        lock (_gate)
        {
            _prefix = null;
            if (!_lineOpen) return;
            output.WriteLine("stopped");
            _lineOpen = false;
        }
    }

    private string Outcome(ImmichDiscoveryProgress.CandidateJudged judged)
    {
        if (judged.Outcome == CandidateOutcome.Issue) return $"issue: {judged.Detail}; {judged.NextStep}";

        var facts = judged.Facts is { } measured ? $"{Describe(measured)}  " : string.Empty;
        return facts + (judged.Outcome == CandidateOutcome.Queued ? Queued(judged) : $"skip: {judged.Detail}");
    }

    // The rule's number is left out when it is the only rule: "fits rule 1" would say nothing.
    private string Queued(ImmichDiscoveryProgress.CandidateJudged judged)
    {
        var fits = judged.Rule is { } rule && _ruleCount > 1 ? $": fits rule {rule}" : string.Empty;
        var count = _limit is { } limit ? $" ({judged.Found} of {limit})" : string.Empty;
        return $"queued{fits}{count}";
    }

    private static string Describe(CandidateFacts facts) =>
        $"{facts.Codec} {Resolution(facts.Width, facts.Height)} {Bitrate(facts.VideoBitsPerSecond)}  {HumanReadableValues.FormatSize(facts.SizeBytes)}";

    /// <summary>
    /// The usual name of the frame's size. It goes by the shorter side so a portrait phone video reads like its landscape
    /// twin, which is also what the eligibility bands treat alike (they compare pixel counts, not orientation).
    /// </summary>
    internal static string Resolution(int? width, int? height)
    {
        if (width is not { } w || height is not { } h) return "?";
        return Math.Min(w, h) switch
        {
            >= 2160 => "4K",
            >= 1440 => "1440p",
            >= 1080 => "1080p",
            >= 720 => "720p",
            _ => $"{w}x{h}"
        };
    }

    /// <summary>Whole Mbps for a figure the eye reads at a glance, one decimal under 10 where a whole number would hide the difference.</summary>
    internal static string Bitrate(long? bitsPerSecond)
    {
        if (bitsPerSecond is not { } bits) return "? Mbps";
        var mbps = bits / 1_000_000d;
        var tenths = Math.Round(mbps, 1, MidpointRounding.AwayFromZero);
        return tenths < 10
            ? $"{tenths.ToString("0.0", CultureInfo.InvariantCulture)} Mbps"
            : $"{Math.Round(mbps, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)} Mbps";
    }

    /// <summary>
    /// The lines that close discovery: what was found, why videos were skipped, what was never downloaded and the rest.
    /// Every count the result holds appears somewhere; zeros do not.
    /// </summary>
    public static IReadOnlyList<string> Summary(ImmichDiscoveryResult result, int? limit)
    {
        var lines = new List<string>();
        var found = result.Queued.Count + result.AlreadyQueued;
        var head = limit is { } wanted
            ? $"Found {found} of {wanted} after checking {result.Checked}."
            : $"Found {OutputWords.Count(found, "video", "videos")} to convert after checking {result.Checked}.";
        lines.Add(result.Skipped > 0 ? $"{head} {SkippedPart(result)}" : head);

        var notDownloaded = new List<string>();
        void Add(int count, string text) { if (count > 0) notDownloaded.Add($"{count} {text}"); }
        Add(result.AlreadyConverted + result.PreviouslyFailed + result.PreviouslySkipped, "handled in earlier runs");
        Add(result.Ineligible, "too small or not a listed file type");
        Add(result.Excluded, "excluded");
        if (notDownloaded.Count > 0) lines.Add($"Not downloaded: {string.Join(", ", notDownloaded)}.");

        var also = new List<string>();
        if (result.Restaged > 0) also.Add($"{result.Restaged} {(result.Restaged == 1 ? "staged original" : "staged originals")} downloaded again");
        if (result.Issues > 0) also.Add($"{OutputWords.Count(result.Issues, "issue", "issues")} (above)");
        if (also.Count > 0) lines.Add($"Also: {string.Join(", ", also)}.");
        return lines;
    }

    // The reasons arrive most common first. Three is what fits a line; the rest are counted, not named.
    private static string SkippedPart(ImmichDiscoveryResult result)
    {
        var reasons = result.SkipReasons;
        if (reasons.Count == 0) return $"Skipped {result.Skipped}.";
        if (reasons.Count == 1) return $"Skipped {result.Skipped}: {reasons[0].Reason}.";

        var parts = reasons.Take(3).Select(reason => $"{reason.Count} {reason.Reason}").ToList();
        var other = reasons.Skip(3).Sum(reason => reason.Count);
        if (other > 0) parts.Add($"{other} other");
        return $"Skipped {result.Skipped}: {string.Join(", ", parts)}.";
    }
}

internal static class OutputWords
{
    public static string Count(int count, string singular, string plural) => $"{count} {(count == 1 ? singular : plural)}";
}

/// <summary>The re-upload guard's lines in <c>immich run</c>, worded like the rest of the run's output.</summary>
internal static class ReuploadGuardReport
{
    public static string FormatProgress(ScanProgress update) => update.Stage switch
    {
        "Issue" => $"Re-upload guard issue: {update.Message}",
        _ => $"Re-upload guard: {update.Stage.ToLowerInvariant()} {update.Message}"
    };

    /// <summary>
    /// One line saying what the guard did, or null when it found nothing: the guard runs in every <c>immich run</c>, and a
    /// line that only says it had nothing to do is noise the user learns to skip.
    /// </summary>
    public static string? Summary(ImmichReuploadGuardResult guard)
    {
        var found = new List<string>();
        if (guard.Trashed.Count > 0)
            found.Add($"{OutputWords.Count(guard.Trashed.Count, "converted original", "converted originals")} {(guard.Trashed.Count == 1 ? "was" : "were")} uploaded again and moved to Immich's trash");
        if (guard.Restored.Count > 0)
            found.Add($"{OutputWords.Count(guard.Restored.Count, "converted original", "converted originals")} {(guard.Restored.Count == 1 ? "is" : "are")} out of Immich's trash, so both versions exist");
        if (guard.Issues.Count > 0) found.Add(OutputWords.Count(guard.Issues.Count, "issue", "issues"));
        return found.Count == 0 ? null : $"Re-upload guard: {string.Join("; ", found)}.";
    }
}
