using System.Globalization;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>What one event becomes on screen: log lines, and a change to the current video's progress line.</summary>
/// <param name="ProgressText">Replaces the progress line when <paramref name="SetProgress"/>; null clears it.</param>
/// <param name="ProgressPercent">The progress line's reading, for its bar; null when the line is not a reading.</param>
/// <param name="WorkerAnswered">The event only says the worker answered a poll: it is not news, so it does not end a quiet spell.</param>
internal sealed record EventEffect(IReadOnlyList<LogEntry> Lines, bool SetProgress, string? ProgressText, RunPhase? Phase, bool? WaitingForWorker, int? ProgressPercent = null, bool WorkerAnswered = false);

/// <summary>
/// Words for the run's events: the facts <see cref="RunEvent"/> carries, in the console's meaning and shorter, since the log is a
/// few rows tall. The console's own lines are <c>ConsoleRunObserver</c> and its message classes; keep the meaning of a line
/// when changing it. The TUI has no command to type, so a line that tells the console user a command says what the next run does.
/// </summary>
internal static class RunEventWords
{
    /// <param name="showFate">A managed worker exists, so the run's worker fate means something; with none it is left out.</param>
    public static EventEffect Of(RunEvent e, bool showFate = true)
    {
        var file = e.SourcePath is null ? "A video" : Clean(Path.GetFileName(e.SourcePath));
        var worker = e.Worker is null ? "the worker" : $"the worker {Clean(e.Worker)}";
        IReadOnlyList<LogEntry> Line(LogKind kind, string text) => [new LogEntry(e.At, kind, Clean(text))];
        EventEffect Say(LogKind kind, string text, RunPhase? phase = null, bool? waiting = null) => new(Line(kind, text), false, null, phase, waiting);
        EventEffect Silent(RunPhase? phase = null) => new([], false, null, phase, null);

        switch (e.Kind)
        {
            case RunEventKind.RunStarted:
                return Say(LogKind.Action, Started(e, showFate), RunPhase.Preparing, waiting: false);
            case RunEventKind.VideoSkipped or RunEventKind.JoiningVideoSkipped:
                return Say(LogKind.Info, $"{file} is left for a later run: it started in the other processing mode.");
            case RunEventKind.FinalisationInterrupted:
                return Say(LogKind.Warning, $"{file} was interrupted while being finalised. Finalise it again to finish.");
            case RunEventKind.VideosHeld:
                return Say(LogKind.Info, e.Count == 1 ? "1 video is held; the run passes over it." : $"{e.Count} videos are held; the run passes over them.");
            case RunEventKind.ImmichRestaging:
                return e.Flag switch
                {
                    null => Say(LogKind.Info, $"Downloading {file} from Immich again."),
                    true => Say(LogKind.Info, $"{file} is back from Immich."),
                    _ => Say(LogKind.Warning, $"{file}: {e.Text}")
                };
            case RunEventKind.ImmichVideosStayQueued:
                var reason = e.Text is null ? "immich.baseUrl isn't set" : $"Immich can't be used ({Clean(e.Text)})";
                return Say(LogKind.Warning, $"{Videos(e.Count ?? 0, "Immich video stays", "Immich videos stay")} queued: {reason}.");
            case RunEventKind.WorkerProgress:
                // Only while the run has not started converting: the same kind reports the delete at the end.
                return Say(LogKind.Info, e.Text ?? string.Empty, waiting: true);
            case RunEventKind.ConversionStarted:
                return new EventEffect(Line(LogKind.Stage, $"Converting {Videos(e.Count ?? 0, "video", "videos")}."), true, null, RunPhase.Converting, false);
            case RunEventKind.Progress:
                return OfProgress(e);
            case RunEventKind.ConversionEnded:
                return new EventEffect([], true, null, RunPhase.Ending, false);
            case RunEventKind.JoiningVideoWaitsForWorker:
                return Say(LogKind.Info, $"{file} waits for the next run: it needs a worker, and this run has none.");
            case RunEventKind.StoppedByUnreachableWorker:
                return Say(LogKind.Error, $"Stopped: {worker} can't be reached, so {Videos(e.Count ?? 0, "video was", "videos were")} not converted.");
            case RunEventKind.StoppedAfter:
                return Say(LogKind.Warning, $"Stopped after {file}; {Videos(e.Count ?? 0, "video is", "videos are")} still waiting.");
            case RunEventKind.Summary:
                return Summary(e);
            case RunEventKind.StoppedWithoutManagedWorker:
                return Say(LogKind.Warning, e.SourcePath is null
                    ? "Stopped now."
                    : $"Stopped now. {file} continues from its last finished step on the next run.");
            case RunEventKind.StoppedNow:
                return Say(LogKind.Warning, StoppedNow(e, file, worker));
            case RunEventKind.WorkerUnreachableAtEnd:
                return Say(LogKind.Error, $"{worker} stopped answering, so {file} resumes from its stage on the next run." + (e.Flag == true ? string.Empty : " It stays up, billed hourly, until it is deleted."));
            case RunEventKind.WorkerKept:
                return WorkerKept(e, worker);
            case RunEventKind.WorkerDeleting:
                return Say(LogKind.Info, "Deleting the worker.");
            case RunEventKind.WorkerDeleteFailed:
                return Say(LogKind.Error, $"Couldn't delete {worker}: {Clean(e.Text ?? "no reason given")} It's still billing hourly.");
            case RunEventKind.WorkerDeleteEnded:
                return Silent();
            case RunEventKind.ManualWorkerUnreachable:
                return Say(LogKind.Error, $"The worker stopped answering, so {file} resumes from its stage on the next run.");
            case RunEventKind.ManualWorkerReminder:
                return Say(LogKind.Info, "The worker's lifecycle is manual: delete it yourself once everything is ready; powering it off still bills.");
            case RunEventKind.RunFailed:
                return Say(LogKind.Error, $"The run failed: {e.Text}");
            default:
                return Silent();
        }
    }

    /// <summary>
    /// Header, step and outcome lines are the log. A reading (a detail line with a percent: an encode's or a transfer's progress)
    /// repeats every few seconds, so the latest one stands as the video's progress line, with its bar and time left, and never
    /// reaches the log. Any other detail line, such as a CRF attempt and its verdict, is a fact the user wants to read back, so
    /// it goes to the log and stands as the progress line until the next; one with a tone, such as an SSH command sent again,
    /// is logged in that tone. A status (which test clip ab-av1 is on) stands as the progress line like a reading, without a bar.
    /// Raw tool lines repeat like readings. A step starts new work, so the previous step's progress line goes.
    /// </summary>
    private static EventEffect OfProgress(RunEvent e)
    {
        var text = Clean(e.Text ?? string.Empty);
        var tone = Enum.TryParse<JobTone>(e.Tone, out var parsedTone) ? parsedTone : JobTone.None;
        switch (Enum.TryParse<JobProgressKind>(e.Progress, out var kind) ? kind : JobProgressKind.Step)
        {
            case JobProgressKind.WorkerAnswered:
                return new EventEffect([], false, null, null, null, WorkerAnswered: true);
            case JobProgressKind.Detail when e.Percent is null:
                return new EventEffect([new LogEntry(e.At, tone == JobTone.None ? LogKind.Detail : LogKindOf(tone), text)], true, text, null, null);
            case JobProgressKind.Detail or JobProgressKind.ToolOutput or JobProgressKind.Status:
                return new EventEffect([], true, text, null, null, e.Percent);
            case JobProgressKind.Header:
                return new EventEffect([new LogEntry(e.At, LogKind.Stage, text)], true, null, null, null);
            default:
                return new EventEffect([new LogEntry(e.At, LogKindOf(tone), text)], true, null, null, null);
        }
    }

    private static LogKind LogKindOf(JobTone tone) => tone switch
    {
        JobTone.Success => LogKind.Success,
        JobTone.Warning => LogKind.Warning,
        JobTone.Failure => LogKind.Error,
        _ => LogKind.Info
    };

    private static string Started(RunEvent e, bool showFate)
    {
        var kind = e.Text switch
        {
            LockHolders.BackgroundRun => "Background run",
            LockHolders.Run => "Run",
            LockHolders.ImmichRun => "Immich run",
            _ => e.Text is null ? "Run" : Clean(e.Text)
        };
        var process = e.Count is { } pid ? $" (process {pid.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        var fate = !showFate ? string.Empty : e.Fate switch
        {
            nameof(WorkerFateChoice.Delete) => " The worker is deleted when the run ends.",
            nameof(WorkerFateChoice.Keep) => " The worker stays up when the run ends.",
            _ => string.Empty
        };
        return $"{kind} started{process}.{fate}";
    }

    private static EventEffect Summary(RunEvent e)
    {
        var failed = e.Failures ?? [];
        var parts = new List<string>();
        if (e.Ready is > 0) parts.Add(e.ToCheck is > 0 ? $"{e.Ready} ready to finalise ({e.ToCheck} to check)" : $"{e.Ready} ready to finalise");
        if (e.Skipped is > 0) parts.Add($"{e.Skipped} not worth converting");
        if (failed.Count > 0) parts.Add($"{failed.Count} failed");
        var line = parts.Count == 0 ? "The run finished; nothing converted." : $"The run finished: {string.Join(", ", parts)}.";
        var lines = new List<LogEntry> { new(e.At, failed.Count > 0 ? LogKind.Warning : LogKind.Success, line) };
        foreach (var job in failed)
        {
            var why = FailureWords.Describe(job.FailureCategory, job.FailureMessage, sentence: false) ?? job.FailureMessage;
            lines.Add(new LogEntry(e.At, LogKind.Error, Clean($"{Path.GetFileName(job.SourcePath)}: {why}")));
        }
        return new EventEffect(lines, false, null, null, null);
    }

    private static string StoppedNow(RunEvent e, string file, string worker)
    {
        var video = e.SourcePath is null ? string.Empty : $" {file} resumes from its stage on the next worker.";
        return e.Fate switch
        {
            nameof(WorkerFate.Delete) => $"Stopped now.{video} Deleting {worker}.",
            nameof(WorkerFate.Keep) => $"Stopped. {worker} stays up, billed hourly.{StillRunning(e, file)}",
            _ => $"Stopped. {worker} stays up, billed hourly, until you decide."
        };
    }

    // A CRF search or encode keeps running on the worker after a stop now.
    private static string StillRunning(RunEvent e, string file) => e.Stage switch
    {
        nameof(JobStatus.CrfSearching) => $" The CRF search of {file} is still running on it.",
        nameof(JobStatus.Encoding) => $" The encode of {file} is still running on it.",
        _ => string.Empty
    };

    private static EventEffect WorkerKept(RunEvent e, string worker)
    {
        // A stop's lines already say the worker stays; only an answer of no adds that it still bills.
        if (e.RunEnd is nameof(RunEnd.StoppedNow) or nameof(RunEnd.WorkerUnreachable))
        {
            return e.Flag == true
                ? new EventEffect([new LogEntry(e.At, LogKind.Warning, Clean($"{Capital(worker)} stays up, billed hourly, until it is deleted."))], false, null, null, null)
                : new EventEffect([], false, null, null, null);
        }
        return new EventEffect([new LogEntry(e.At, LogKind.Warning, Clean($"{Capital(worker)} stays up, billed hourly, until it is deleted."))], false, null, null, null);
    }

    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private static string Videos(int count, string one, string many) =>
        count == 1 ? $"1 {one}" : $"{count.ToString(CultureInfo.InvariantCulture)} {many}";

    /// <summary>One line of printable text: tool output and file names are not ours, and a control character would move the cursor.</summary>
    public static string Clean(string text)
    {
        var cleaned = text.Any(char.IsControl) ? string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c)) : text;
        return cleaned.Trim();
    }
}
