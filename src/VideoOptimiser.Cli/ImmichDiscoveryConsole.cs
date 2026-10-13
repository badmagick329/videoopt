using System.Globalization;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli;

/// <summary>
/// Prints Immich discovery as it happens. A user once saw 15 downloads scroll past and concluded the tool downloads every
/// video, when each rejected download was deleted at once; so the output opens by saying what discovery does and why, shows
/// one line per downloaded candidate and ends with what was found. A candidate's line is written in two parts, the file
/// before its download starts and the outcome when known, so a slow download shows which file it is on.
/// </summary>
internal sealed class ImmichDiscoveryConsole(TextWriter output, TextWriter error) : IProgress<ImmichDiscoveryProgress>
{
    private readonly object _gate = new();
    private int? _limit;
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
                    output.WriteLine(started.Limit is { } limit
                        ? $"Looking for up to {OutputWords.Count(limit, "video", "videos")} to convert (already queued: {started.AlreadyQueued})."
                        : $"Looking for every eligible video (already queued: {started.AlreadyQueued}).");
                    output.WriteLine("Each candidate is downloaded so ffprobe can check it against your eligibility rules.");
                    output.WriteLine("A video that doesn't match is deleted at once and never downloaded again.");
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
                    var size = candidate.SizeBytes is { } bytes ? HumanReadableValues.FormatSize(bytes) : "size unknown";
                    _prefix = $"[{candidate.Number}] {candidate.FileName}  {candidate.Taken.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, {size}  ";
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

    private string Outcome(ImmichDiscoveryProgress.CandidateJudged judged) => judged.Outcome switch
    {
        CandidateOutcome.Queued => _limit is { } limit ? $"queued ({judged.Found} of {limit})" : "queued",
        CandidateOutcome.Skipped => $"skipped: {judged.Detail}",
        _ => $"issue: {judged.Detail}; {judged.NextStep}"
    };

    /// <summary>
    /// The lines that close discovery: what was found and why the rest was not, the most common reason with the setting that
    /// changes it, and the remaining counts. Every count the result holds appears somewhere; zeros do not.
    /// </summary>
    public static IReadOnlyList<string> Summary(ImmichDiscoveryResult result, int? limit)
    {
        var lines = new List<string>();
        var found = result.Queued.Count + result.AlreadyQueued;
        var rejected = result.Skipped > 0 ? $"; {result.Skipped} didn't match your rules and won't be checked again." : ".";
        lines.Add(limit is { } wanted
            ? $"Found {found} of {wanted} after checking {OutputWords.Count(result.Checked, "video", "videos")}{rejected}"
            : $"Found {OutputWords.Count(found, "video", "videos")} to convert after checking {result.Checked}{rejected}");
        if (result.SkipReasons.Count > 0)
            lines.Add($"Most common reason: {result.SkipReasons[0].Reason} ({result.SkipReasons[0].Count}). To include those, change eligibility.rules.");

        var also = new List<string>();
        void Add(int count, string text) { if (count > 0) also.Add($"{count} {text}"); }
        Add(result.AlreadyConverted, "already converted");
        Add(result.PreviouslyFailed, "previously failed or cancelled");
        Add(result.PreviouslySkipped, "previously skipped");
        Add(result.Ineligible, "ineligible by extension or size");
        Add(result.Excluded, "excluded");
        Add(result.Restaged, result.Restaged == 1 ? "staged original downloaded again" : "staged originals downloaded again");
        Add(result.Issues, result.Issues == 1 ? "issue" : "issues");
        if (also.Count > 0) lines.Add($"Also: {string.Join(", ", also)}.");
        return lines;
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

    /// <summary>One line; counts that are zero are left out, and a guard that found nothing says so.</summary>
    public static string Summary(ImmichReuploadGuardResult guard)
    {
        var found = new List<string>();
        if (guard.Trashed.Count > 0) found.Add($"{guard.Trashed.Count} trashed");
        if (guard.Restored.Count > 0) found.Add($"{guard.Restored.Count} restored");
        if (guard.Issues.Count > 0) found.Add(OutputWords.Count(guard.Issues.Count, "issue", "issues"));
        return $"Re-upload guard: checked {OutputWords.Count(guard.Checked, "converted original", "converted originals")}; " +
               (found.Count == 0 ? "nothing uploaded again." : $"{string.Join(", ", found)}.");
    }
}
