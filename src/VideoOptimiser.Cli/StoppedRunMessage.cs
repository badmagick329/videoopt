using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli;

/// <summary>
/// What Ctrl+C prints (<see cref="RunStop"/>). A stop now on a remote run stops only this side: a CRF search or encode keeps
/// running on the worker, which bills hourly until it is deleted, so <see cref="Lines"/> say what is still running, what it
/// costs, and the command that picks it up again. The wording was approved by the user (VOPT-47, and VOPT-54 step 3 for the
/// two presses); ask before changing it.
/// </summary>
internal static class StoppedRunMessage
{
    /// <summary>The first press while a video converts. The only estimate there is covers the encode, so the line names it as the encode's.</summary>
    public static string StoppingAfterThisVideo(TimeSpan? encodeTimeLeft) => encodeTimeLeft is { } left
        ? $"Stopping after this video (the encode has about {HumanReadableValues.FormatDuration(left)} left). Press Ctrl+C again to stop now."
        : "Stopping after this video. Press Ctrl+C again to stop now.";

    /// <summary>A run that stopped after a video, with videos it never started.</summary>
    public static string StoppedAfter(string sourcePath, int waiting) =>
        $"Stopped after {Path.GetFileName(sourcePath)}; {OutputWords.Count(waiting, "video is", "videos are")} still waiting.";

    public static string ConvertTheRest(CliCommand command, int? immichJobsLeft) => $"To convert the rest: video-optimiser {RerunCommand(command, immichJobsLeft)}";

    /// <summary>
    /// A stop now that leaves nothing running on a worker: on this PC, between a manual host's remote stages, or before any
    /// worker existed. The video resumes from its last finished step: a saved CRF is kept, a local encode starts again.
    /// </summary>
    /// <param name="interrupted">The video Ctrl+C stopped, or null when none was converting (Immich discovery, a prompt).</param>
    public static IReadOnlyList<string> StoppedNow(JobInterruptedException? interrupted, CliCommand command, int? immichJobsLeft = null) =>
    [
        interrupted is null ? "Stopped now." : $"Stopped now. {Path.GetFileName(interrupted.Job.SourcePath)} continues from its last finished step on the next run.",
        $"To resume: video-optimiser {RerunCommand(command, immichJobsLeft)}"
    ];

    /// <param name="interrupted">The job Ctrl+C stopped, or null when it came between jobs or before the first one.</param>
    /// <param name="worker">The managed server's name, or the SSH host of a manual one.</param>
    /// <param name="managed">A manual host is not billed or deleted by this tool, so it gets only the lines that hold for it.</param>
    /// <param name="immichJobsLeft">See <see cref="RerunCommand"/>.</param>
    public static IReadOnlyList<string> Lines(JobInterruptedException? interrupted, string worker, bool managed, CliCommand command, int? immichJobsLeft = null)
    {
        var rerun = $"video-optimiser {RerunCommand(command, immichJobsLeft)}";
        var delete = DeleteCommand(command);
        var stillRunning = interrupted is { Remote: true, Stage: JobStatus.CrfSearching or JobStatus.Encoding }
            ? $"{(interrupted.Stage == JobStatus.Encoding ? "The encode" : "The CRF search")} of {Path.GetFileName(interrupted.Job.SourcePath)} is still running on the worker {worker}."
            : null;
        if (!managed) return stillRunning is null ? [] : [$"Stopped watching. {stillRunning}", $"To pick it up again: {rerun}"];
        if (stillRunning is not null)
        {
            return
            [
                $"Stopped watching. {stillRunning}",
                "The worker is billed hourly until it is deleted.",
                $"To pick it up again: {rerun}",
                $"To stop it instead: {delete} (the work in progress is lost)"
            ];
        }
        // The finished encode exists only on the worker until it is downloaded; a rerun after deleting it encodes again.
        var downloading = interrupted is { Remote: true, Stage: JobStatus.Downloading };
        return
        [
            downloading
                ? $"Stopped. The encoded output is waiting on the worker {worker}, which stays up, billed hourly."
                : $"Stopped. Nothing is running on the worker {worker}, but it stays up, billed hourly, so the job can resume.",
            $"To resume: {rerun}",
            $"To delete it instead: {delete}{(downloading ? " (the finished encode is lost)" : string.Empty)}"
        ];
    }

    /// <summary>
    /// A stop now on a managed worker, with the question still to ask: <see cref="Lines"/> without its delete line, which the
    /// question replaces. With nothing running there, the first line no longer says the worker stays "so the job can resume":
    /// a video resumes on a new worker too.
    /// </summary>
    public static IReadOnlyList<string> LinesBeforeQuestion(JobInterruptedException? interrupted, string worker, CliCommand command, int? immichJobsLeft = null)
    {
        var lines = Lines(interrupted, worker, managed: true, command, immichJobsLeft).SkipLast(1).ToList();
        if (Lost(interrupted?.Remote == true ? interrupted.Stage : null) is null) lines[0] = $"Stopped. Nothing is running on the worker {worker}, but it stays up, billed hourly.";
        return lines;
    }

    /// <param name="remoteStage">The stage the video stopped in on the worker, or null when it was not on the worker.</param>
    public static string DeleteNowQuestion(JobStatus? remoteStage) => Lost(remoteStage) switch
    {
        LostWork.InProgress => $"Delete the worker now? The {(remoteStage == JobStatus.Encoding ? "encode" : "CRF search")} in progress is lost. [y/N] ",
        LostWork.FinishedEncode => "Delete the worker now? The finished encode is lost. [y/N] ",
        _ => WorkerEndMessage.QuestionAtEnd
    };

    /// <summary>The answer N to <see cref="DeleteNowQuestion"/>.</summary>
    public static string DeleteLater(JobStatus? remoteStage, CliCommand command) => $"To delete it later: {DeleteCommand(command)}{LostSuffix(remoteStage)}";

    /// <summary>What deleting the worker throws away when a video stopped in <paramref name="remoteStage"/> on it.</summary>
    public static string LostSuffix(JobStatus? remoteStage) => Lost(remoteStage) switch
    {
        LostWork.InProgress => " (the work in progress is lost)",
        LostWork.FinishedEncode => " (the finished encode is lost)",
        _ => string.Empty
    };

    private enum LostWork { InProgress, FinishedEncode }

    // A CRF search or encode keeps running on the worker after a stop now; a finished encode waits there until downloaded.
    private static LostWork? Lost(JobStatus? remoteStage) => remoteStage switch
    {
        JobStatus.CrfSearching or JobStatus.Encoding => LostWork.InProgress,
        JobStatus.Downloading => LostWork.FinishedEncode,
        _ => null
    };

    /// <summary>The command the user ran, with the flags that change what it does; <c>--config</c> only when it was typed.</summary>
    /// <param name="immichJobsLeft">
    /// The Immich jobs a rerun would pick up (queued, or interrupted outside finalisation). <c>immich run --limit N</c> tops the
    /// open jobs up to N, so rerunning with the N the user typed would download new videos on top of the ones left; a typed
    /// <c>--limit</c> or <c>--first</c> becomes <c>--limit</c> with this count instead (VOPT-69). Null or 0 keeps the typed flag.
    /// </param>
    public static string RerunCommand(CliCommand command, int? immichJobsLeft = null)
    {
        var verb = command.Kind switch
        {
            CliCommandKind.Process => $"process \"{command.ProcessPath}\"{(command.Force ? " --force" : string.Empty)}",
            CliCommandKind.QueueRun => "queue run",
            CliCommandKind.ImmichRun when command.Limit is not null && immichJobsLeft > 0 => $"immich run --limit {immichJobsLeft}",
            // --first is parsed as --limit 1, so the flag the user typed is checked first.
            CliCommandKind.ImmichRun => $"immich run{(command.First ? " --first" : command.Limit is { } limit ? $" --limit {limit}" : string.Empty)}",
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Only processing commands can be stopped mid-run.")
        };
        return $"{verb}{(command.ModeOverride is null ? string.Empty : $" {command.ModeOverride.Flag}")}{ConfigArgument(command)}";
    }

    /// <summary>
    /// The Immich jobs a rerun would pick up: queued, or interrupted outside finalisation, the set restaging and the limit
    /// count. Not every open Immich job: ready and finalising ones would inflate the limit, and the rerun would download that
    /// many new videos.
    /// </summary>
    public static int CountImmichJobsLeft(IEnumerable<JobRecord> openJobs) =>
        openJobs.Count(job => job.SourceKind == JobSourceKind.Immich && job.Status is JobStatus.Queued or JobStatus.Interrupted && job.ResumeStatus != JobStatus.Finalizing);

    public static string DeleteCommand(CliCommand command) => $"video-optimiser worker delete{ConfigArgument(command)}";

    private static string ConfigArgument(CliCommand command) =>
        command.ConfigurationPath is null ? string.Empty : $" --config \"{command.ConfigurationPath}\"";
}
