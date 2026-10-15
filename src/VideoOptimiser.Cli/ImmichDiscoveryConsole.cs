using System.Globalization;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.ImmichRun;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli;

/// <summary>
/// Prints Immich discovery as it happens. A user once saw 15 downloads scroll past and concluded the tool downloads every
/// video, when each rejected download was deleted at once; so the first download is preceded by what discovery does, each
/// downloaded candidate gets one line and the output ends with what was found. A candidate's line is written in two parts, the file and date
/// before its download starts and the rest when known, so a slow download shows which file it is on. The rest names the
/// candidate's codec, resolution, bitrate and size before the verdict, because a verdict such as "rule 1 needs 2GiB" is
/// only readable beside the facts it was reached from: one user could not see why a 319 MiB video was queued while two
/// others were skipped for being under 2 GiB, when the difference was the codec. On a terminal the explanation is dim and each
/// verdict has a colour (queued green, doesn't fit yellow, issue red), so the few lines that matter stand out of a long run.
/// It also words the restaging <c>run</c> does, which reports through the same events.
/// </summary>
internal sealed class ImmichDiscoveryConsole(TextWriter output, TerminalStyle outputStyle, TextWriter error, TerminalStyle errorStyle) : IProgress<ImmichProgress>
{
    private readonly object _gate = new();
    private int? _limit;
    private int _ruleCount;
    // The explanation comes with the first download, so a search that downloads nothing never shows it.
    private bool _explained;
    // The open candidate's line so far; set while its download runs, null once its outcome is out.
    private string? _prefix;
    private bool _lineOpen;

    public void Report(ImmichProgress value)
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
                    output.WriteLine(outputStyle.Dim(started.Limit is { } limit
                        ? $"Looking in Immich for up to {OutputWords.Count(limit, "new video", "new videos")} to convert."
                        : "Looking in Immich for every video that fits your rules."));
                    if (started.StaleSkips > 0)
                        // The count is of records, not of a change event: a search with a limit re-checks only the stale skips it
                        // reaches, so the rest stay stale and this line returns on later runs with the rules unchanged.
                        output.WriteLine($"{OutputWords.Count(started.StaleSkips, "video that didn't", "videos that didn't")} fit your earlier rules {(started.StaleSkips == 1 ? "is" : "are")} checked again under the current ones when the search reaches {(started.StaleSkips == 1 ? "it" : "them")}.");
                    break;
                case ImmichProgress.Restaging restaging:
                    output.WriteLine($"Downloading again: {restaging.FileName} (job {restaging.JobId:N} is open but its staged copy is missing)");
                    break;
                case ImmichProgress.Restaged restaged:
                    output.WriteLine(restaged.Problem is null ? outputStyle.Green("  done: the staged copy is back") : outputStyle.Red($"  issue: {restaged.Problem}"));
                    break;
                case ImmichDiscoveryProgress.CandidateDownloading candidate:
                    if (!_explained)
                    {
                        // "The download", not "it": a user read "deleted" as the video in Immich.
                        output.WriteLine(outputStyle.Dim("Each video below is downloaded and checked. If it doesn't fit your rules, the download is deleted and the video is not checked again until your rules change."));
                        output.WriteLine();
                        _explained = true;
                    }
                    _prefix = $"  {candidate.Number}. {candidate.FileName}  {candidate.Taken.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}  ";
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
                    error.WriteLine(errorStyle.Red($"Left alone: {excluded.FileName} (Immich's search should not have returned it: it {excluded.Reason})."));
                    break;
                case ImmichProgress.AssetIssue issue:
                    output.WriteLine(outputStyle.Red($"Issue: {issue.FileName}: {issue.Message}."));
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
        if (judged.Outcome == CandidateOutcome.Issue) return outputStyle.Red($"issue: {judged.Detail}; {judged.NextStep}");

        // The facts stay plain: only the verdict is coloured, because it is what the eye is looking for.
        var facts = judged.Facts is { } measured ? $"{Describe(measured)}  " : string.Empty;
        return facts + (judged.Outcome == CandidateOutcome.Queued ? outputStyle.Green(Queued(judged)) : outputStyle.Yellow($"doesn't fit: {judged.Detail}"));
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
    /// The lines that close discovery: what was downloaded and checked, why videos did not fit, what was never downloaded and
    /// the issues. Zeros do not appear. The queue's counts follow, printed by the command.
    /// </summary>
    public static IReadOnlyList<string> Summary(ImmichDiscoveryResult result, int? limit)
    {
        // Sets the lines apart from the candidates, and from what was said while downloading, when there was any.
        var lines = new List<string>();
        if (result.Checked + result.Issues + result.Excluded > 0) lines.Add(string.Empty);
        if (result.Checked > 0) lines.Add(Checked(result));

        var notDownloaded = new List<string>();
        void Add(int count, string text) { if (count > 0) notDownloaded.Add($"{count} {text}"); }
        Add(result.AlreadyConverted + result.PreviouslyFailed + result.PreviouslySkipped, "handled in earlier runs");
        Add(result.Ineligible, "too small or not a file type your rules list");
        if (notDownloaded.Count > 0) lines.Add($"Not downloaded: {string.Join(", ", notDownloaded)}.");

        // The search stops at the limit, so a count below it means Immich had nothing more to give.
        if (limit is { } wanted && result.Queued.Count < wanted) lines.Add("Immich has no more videos that fit your rules.");
        if (result.Issues > 0) lines.Add($"Also: {OutputWords.Count(result.Issues, "issue", "issues")} (above).");
        return lines;
    }

    private static string Checked(ImmichDiscoveryResult result)
    {
        var parts = new List<string>();
        if (result.Queued.Count > 0) parts.Add($"{result.Queued.Count} queued");
        if (result.Skipped > 0) parts.Add($"{result.Skipped} {(result.Skipped == 1 ? "doesn't" : "don't")} fit your rules ({SkipReasons(result)})");
        return $"Downloaded and checked {OutputWords.Count(result.Checked, "video", "videos")}: {string.Join(", ", parts)}.";
    }

    // The reasons arrive most common first. Three is what fits a line; the rest are counted, not named.
    private static string SkipReasons(ImmichDiscoveryResult result)
    {
        var reasons = result.SkipReasons;
        if (reasons.Count == 1) return reasons[0].Reason;

        var parts = reasons.Take(3).Select(reason => $"{reason.Count} {reason.Reason}").ToList();
        var other = reasons.Skip(3).Sum(reason => reason.Count);
        if (other > 0) parts.Add($"{other} other");
        return string.Join(", ", parts);
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
