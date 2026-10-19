using System.Globalization;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Queue;

/// <summary>
/// The words the queue commands share, for what the Application use cases decided. Changed wording goes through the
/// humanizer gate (docs index).
/// </summary>
internal static class QueueWords
{
    /// <summary>
    /// What to run once videos are queued. A run going now takes them at its next video; a run from an older version keeps
    /// the videos it started with, so they wait for it to end.
    /// </summary>
    public static string NextRunLine(NextRun nextRun, CliCommand command) => nextRun switch
    {
        NextRun.NoRun => $"Next: run '{command.Hint("run")}'.",
        NextRun.RunTakesThem => "The run going now picks them up.",
        _ => $"Next: run '{command.Hint("run")}' once the run going now ends."
    };

    public static string StatusPhrase(JobRecord job) => job.Status switch
    {
        JobStatus.ReadyToFinalize => "is ready to finalise",
        JobStatus.Failed => "has already failed",
        JobStatus.Cancelled => "was already dropped",
        JobStatus.Completed => "was already finalised",
        JobStatus.Skipped when SkippedForAudio(job) => "was skipped for its audio",
        JobStatus.Skipped => "was not worth converting",
        JobStatus.Rejected => "was already rejected",
        _ => $"is {StatusWord(job.Status)}"
    };

    /// <summary>The files a reject deleted or left, as the end of "Deleted its …".</summary>
    public static string FilesPhrase(RejectedFiles files) => (files.Av1, files.DownloadedCopy) switch
    {
        (true, true) => "AV1 and downloaded copy",
        (true, false) => "AV1",
        _ => "downloaded copy"
    };

    /// <summary>The glossary's word for a status, lower case for use inside a sentence.</summary>
    public static string StatusWord(JobStatus status) => status switch
    {
        JobStatus.Queued => "waiting",
        JobStatus.Staging => "uploading",
        JobStatus.CrfSearching => "finding CRF",
        JobStatus.Encoding => "encoding",
        JobStatus.Downloading => "downloading",
        JobStatus.Validating => "checking",
        JobStatus.ReadyToFinalize => "ready to finalise",
        JobStatus.Finalizing => "finalising",
        JobStatus.Completed => "finalised",
        JobStatus.Interrupted => "stopped",
        JobStatus.Cancelled => "dropped",
        JobStatus.Skipped => "not worth converting",
        JobStatus.Rejected => "rejected",
        _ => status.ToString().ToLowerInvariant()
    };

    /// <summary>The same word starting a column, as <c>history</c> lists it.</summary>
    public static string StatusName(JobRecord job)
    {
        var word = SkippedForAudio(job) ? "skipped" : StatusWord(job.Status);
        return char.ToUpperInvariant(word[0]) + word[1..];
    }

    /// <summary>Skipped for audio FFmpeg cannot copy, never tried, so "not worth converting" would be false (VOPT-125).</summary>
    public static bool SkippedForAudio(JobRecord job) => job.Status == JobStatus.Skipped && job.FailureCategory == FailureCategories.UnsupportedAudio;

    /// <summary>The open status in the glossary's words.</summary>
    public static string OpenStatusPhrase(JobRecord job, OpenJobState state) => state switch
    {
        OpenJobState.Held => "held",
        OpenJobState.ReadyToFinalize => "ready to finalise",
        OpenJobState.StoppedWhileFinalising => "stopped while being finalised",
        OpenJobState.Finalising => "finalising",
        OpenJobState.Stopped => "stopped",
        _ => StatusWord(job.Status)
    };

    private static readonly (OpenJobGroup Group, string Header)[] OpenJobGroups =
    [
        (OpenJobGroup.ConvertingNow, "Converting now"),
        (OpenJobGroup.Queue, "Queue, in run order"),
        (OpenJobGroup.Held, "Held"),
        (OpenJobGroup.ReadyToFinalise, "Ready to finalise")
    ];

    /// <summary>
    /// <c>queue list</c>: a header per group that has jobs, then one indented line each. The queue is
    /// numbered, and the first two groups carry a status column padded to the widest in the group.
    /// </summary>
    public static IEnumerable<string> OpenJobLines(IReadOnlyList<OpenJobEntry> entries, CliCommand command)
    {
        foreach (var (group, header) in OpenJobGroups)
        {
            var inGroup = entries.Where(entry => entry.Group == group).ToArray();
            if (inGroup.Length == 0) continue;
            yield return header;
            var statusWidth = group is OpenJobGroup.ConvertingNow or OpenJobGroup.Queue ? inGroup.Max(entry => StatusColumn(entry).Length) : 0;
            var numberWidth = inGroup.Length.ToString(CultureInfo.InvariantCulture).Length + 1;
            for (var index = 0; index < inGroup.Length; index++)
            {
                var entry = inGroup[index];
                var number = group == OpenJobGroup.Queue ? $"{$"{index + 1}.".PadLeft(numberWidth)} " : string.Empty;
                var status = statusWidth > 0 ? $"{StatusColumn(entry).PadRight(statusWidth)}  " : string.Empty;
                yield return $"  {number}{entry.Job.Id:N}  {status}{OpenJobDetail(entry, command)}";
            }
        }
    }

    private static string StatusColumn(OpenJobEntry entry)
    {
        var word = entry.Stopped is null ? StatusWord(entry.Job.Status) : "stopped";
        return char.ToUpperInvariant(word[0]) + word[1..];
    }

    /// <summary>What is known of the job, then what comes next for it.</summary>
    private static string OpenJobDetail(OpenJobEntry entry, CliCommand command)
    {
        var job = entry.Job;
        // An Immich job's file name is the original's in Immich.
        var facts = new List<string> { Path.GetFileName(job.SourcePath) + (job.SourceKind == JobSourceKind.Immich ? " (Immich)" : string.Empty) };
        if (job.Crf is { } crf) facts.Add($"CRF {crf}");
        if (job.PercentageSaved is { } saved) facts.Add($"{saved:F1}% saved");
        if (entry.Group == OpenJobGroup.Held && entry.Stopped is not null) facts.Add("stopped");
        if (entry.State == OpenJobState.StoppedWhileFinalising) facts.Add("stopped while being finalised");
        var line = string.Join(", ", facts);
        // In full, as the run printed them: the person reads them here before choosing to finalise the video on its own.
        if (FinalizationRules.IsLeftForReview(job)) line += $", {OutputWords.Count(job.ValidationWarnings.Count, "warning", "warnings")}: {string.Join(' ', job.ValidationWarnings)}";
        // A note is no warning: it never marks the video for review, so it is listed after any warnings under its own word.
        if (job is { Status: JobStatus.ReadyToFinalize, Notes.Count: > 0 }) line += $", {OutputWords.Count(job.Notes.Count, "note", "notes")}: {string.Join(' ', job.Notes)}";
        return entry.State switch
        {
            OpenJobState.LastFinaliseFailed => $"{line}; the last finalise stopped: {job.FailureMessage}",
            OpenJobState.StoppedWhileFinalising => $"{line}; '{command.Hint($"finalize {job.Id:N}")}' checks and resumes it",
            OpenJobState.Finalising => $"{line}, being finalised now",
            _ when entry is { Group: OpenJobGroup.Queue, Stopped: { } stopped } => $"{line}: {(stopped.WorkerUnreachable ? "the worker couldn't be reached; " : string.Empty)}{StoppedEnding(stopped)}",
            _ => line
        };
    }

    /// <summary>What the next run does with a stopped job, by the step it stopped in and whether its work can still be on a worker.</summary>
    private static string StoppedEnding(StoppedJob stopped)
    {
        if (stopped.Step == ResumeStep.Beginning) return "starts from the beginning";
        if (stopped.Step == ResumeStep.Check) return "checks the AV1 again";
        if (stopped.Where == ResumeWhere.Local) return stopped.Step == ResumeStep.Encode ? "starts the encode again" : "searches for the CRF again";
        var (pickUp, again) = stopped.Step switch
        {
            ResumeStep.Upload => ("picks up the upload", "uploads again to a new worker"),
            ResumeStep.CrfSearch => ("picks up the CRF search on the worker", "searches again on a new one"),
            ResumeStep.Encode => ("picks up the encode on the worker", "encodes again on a new one"),
            _ => ("downloads the AV1 from the worker", "encodes again on a new one")
        };
        // A managed worker may be gone by the next run, which then starts the step again on a new one; a manual one is the same host.
        return stopped.Where == ResumeWhere.ManagedWorker ? $"{pickUp}, or {again}" : pickUp;
    }

    // ---- a queue verb's outcome, for the CLI's queue commands and the TUI's queue keys ----------------------------------
    // One sentence serves both front ends, so they cannot drift (VOPT-174). Each passes in what differs: the CLI names a
    // command to type, quoted from CliCommand.Hint; the TUI has no command line, so it names a key or the command in plain words.

    /// <summary>Why a verb did not act.</summary>
    /// <param name="notWaitingAction">How the verb ends "Only waiting and stopped videos can …", such as <c>be held</c>; null for a verb that never refuses a job for not waiting.</param>
    /// <param name="howToStop">How to stop the run converting the video: <c>run stop</c> or Ctrl+C in the CLI, the stop keys in the TUI.</param>
    /// <param name="finaliseIt">What checks and resumes a stopped finalisation, as the subject of "… checks and resumes it".</param>
    public static string RefusalText(VerbRefused refused, string? notWaitingAction, string howToStop, string finaliseIt)
    {
        var job = refused.Job;
        var name = Path.GetFileName(job.SourcePath);
        return refused.Reason switch
        {
            VerbRefusal.ConvertingNow => $"{name} is converting now ({StatusWord(job.Status)}). {howToStop}",
            VerbRefusal.StoppedWhileFinalising => $"{name} was stopped while being finalised; {finaliseIt} checks and resumes it.",
            VerbRefusal.BeingFinalised => $"{name} is being finalised now.",
            VerbRefusal.NotReady => $"Only a video that is ready to finalise can be rejected; {name} {StatusPhrase(job)}.",
            VerbRefusal.NotFailed => $"Only a failed video can be dismissed; {name} {StatusPhrase(job)}.",
            _ => $"Only waiting and stopped videos can {notWaitingAction ?? throw new InvalidOperationException("This verb never refuses a job for not waiting.")}; {name} {StatusPhrase(job)}."
        };
    }

    /// <summary>The refusals that protect a video whose AV1 may be the only copy keep their meaning and their paths.</summary>
    /// <param name="verb">The verb as it ends "then … the video", such as <c>drop</c>.</param>
    public static string OnlyCopyText(OnlyCopyAtRisk onlyCopy, string verb)
    {
        var job = onlyCopy.Job;
        var name = Path.GetFileName(job.SourcePath);
        return onlyCopy.Reason switch
        {
            OnlyCopyReason.OriginalMissing => $"The original of {name} is missing, so its AV1 at {job.OutputPath} may be the only copy. Put the original back, or move the AV1 somewhere safe, then {verb} the video.",
            OnlyCopyReason.ImmichNotConfigured => ImmichUnusable("immich.baseUrl isn't set", name),
            OnlyCopyReason.ImmichUnavailable => ImmichUnusable(onlyCopy.Detail!.TrimEnd('.'), name),
            _ => $"The original of {name} is gone from Immich, so its AV1 and downloaded copy may be the only copies: {job.OutputPath}, {job.SourcePath}. Move them somewhere safe, then {verb} the video."
        };
    }

    private static string ImmichUnusable(string why, string name) =>
        $"Immich can't be used ({why}), so video-optimiser can't check that the original of {name} is still there. Try again once it answers.";

    /// <summary>
    /// W3: a remote CRF search or encode runs detached, so a stop leaves it going. The next run that reaches the worker, managed
    /// or manual, stops it and removes its workspace (VOPT-90), so one line serves both.
    /// </summary>
    public static string RemoteStageLine(RemoteStageNote note) =>
        $"Its {(note.Stage == RemoteStage.Encode ? "encode" : "CRF search")} may still be running on the worker; the next run on that worker stops it.";

    /// <summary>The files a drop left for cleanup.</summary>
    /// <param name="cleanup">How the front end names the <c>cleanup</c> command, as the subject of "… deletes it".</param>
    public static string LeftFilesLine(LeftFiles files, string cleanup)
    {
        var av1 = files.Av1 switch
        {
            LeftAv1.Finished => "AV1",
            LeftAv1.Unfinished => "unfinished AV1",
            _ => null
        };
        return (av1, files.DownloadedCopy) switch
        {
            (null, _) => $"Its downloaded copy stays until {cleanup} deletes it.",
            (_, false) => $"Its {av1} stays until {cleanup} deletes it.",
            _ => $"Its {av1} and downloaded copy stay until {cleanup} deletes them."
        };
    }

    /// <summary>A file a reject could not delete is almost always open in a player; the reject stands and cleanup finishes the job.</summary>
    /// <param name="cleanup">How the front end names the <c>cleanup</c> command, as the subject of "… deletes it".</param>
    public static string LeftForCleanupLine(RejectedFiles left, string cleanup)
    {
        var both = left is { Av1: true, DownloadedCopy: true };
        return $"Couldn't delete its {FilesPhrase(left)}; {(both ? "they may be" : "it may be")} open in another program. {(both ? "They stay" : "It stays")} until {cleanup} deletes {(both ? "them" : "it")}.";
    }

    /// <summary>After "there is nothing to drop": an ended job whose temporary files cleanup would still delete.</summary>
    /// <param name="cleanup">How the front end names the <c>cleanup</c> command; it starts the sentence, so its first letter is raised.</param>
    public static string TemporaryFilesLine(string cleanup) => $"{char.ToUpperInvariant(cleanup[0])}{cleanup[1..]} deletes its temporary files.";

    /// <summary>A scanner reason ("Extension is not allowed.") as the end of a sentence.</summary>
    public static string Fragment(string reason) => reason.Length == 0 ? reason : char.ToLowerInvariant(reason[0]) + reason[1..].TrimEnd('.');

}
