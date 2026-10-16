using System.Globalization;
using VideoOptimiser.Application.Features.Queue;
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
    public static string StatusName(JobStatus status)
    {
        var word = StatusWord(status);
        return char.ToUpperInvariant(word[0]) + word[1..];
    }

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

    /// <summary>A scanner reason ("Extension is not allowed.") as the end of a sentence.</summary>
    public static string Fragment(string reason) => reason.Length == 0 ? reason : char.ToLowerInvariant(reason[0]) + reason[1..].TrimEnd('.');

}
