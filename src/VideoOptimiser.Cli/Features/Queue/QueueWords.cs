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
    /// What to run once videos are queued. A run going now takes them at its next video; an <c>immich run</c> keeps the
    /// videos it started with (until phase A step 7), as does a run from an older version, so they wait for it to end.
    /// </summary>
    public static string NextRunLine(NextRun nextRun, CliCommand command) => nextRun switch
    {
        NextRun.NoRun => $"Next: run '{command.Hint("run")}'.",
        NextRun.RunTakesThem => "The run going now picks them up.",
        _ => $"Next: run '{command.Hint("run")}' once the run going now ends."
    };

    public static string ReadyToFinaliseLine(int count, CliCommand command) => count == 1
        ? $"1 video is ready to finalise: review it, then run '{command.Hint("finalize --ready")}'."
        : $"{count} videos are ready to finalise: review them, then run '{command.Hint("finalize --ready")}'.";

    public static string StatusPhrase(JobRecord job) => job.Status switch
    {
        JobStatus.ReadyToFinalize => "is ready to finalise",
        JobStatus.Failed => "has already failed",
        JobStatus.Cancelled => "was already dropped",
        JobStatus.Completed => "was already finalised",
        JobStatus.Skipped => "was not worth converting",
        _ => $"is {StatusWord(job.Status)}"
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
        JobStatus.Finalizing => "finalising",
        JobStatus.Interrupted => "stopped",
        _ => status.ToString().ToLowerInvariant()
    };

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

    /// <summary>A scanner reason ("Extension is not allowed.") as the end of a sentence.</summary>
    public static string Fragment(string reason) => reason.Length == 0 ? reason : char.ToLowerInvariant(reason[0]) + reason[1..].TrimEnd('.');

    public static string ConfigArgument(CliCommand command) => command.ConfigurationPath is null ? string.Empty : $" --config \"{command.ConfigurationPath}\"";
}
