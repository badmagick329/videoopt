using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// What a run prints when the worker stops answering (VOPT-69). The run stops at that job, which resumes from its stage on
/// the rerun, and the rest stay queued. The wording was approved by the user (fix-now batch U2, U3, U3m); ask before changing it.
/// </summary>
internal static class UnreachableWorkerMessage
{
    /// <summary>U2, printed by <c>run</c> and <c>immich run</c>.</summary>
    /// <param name="notConverted">The interrupted job plus the jobs the run never started.</param>
    public static string Stopped(string worker, int notConverted) =>
        $"Stopped: the worker {worker} can't be reached, so {(notConverted == 1 ? "1 video was" : $"{notConverted} videos were")} not converted.";

    /// <summary>U3 for a managed worker. A manual host is neither billed nor deleted by this tool, so it gets only the rerun (U3m).</summary>
    public static IReadOnlyList<string> Lines(JobRecord job, bool managed, CliCommand command)
    {
        var resume = $"To resume: video-optimiser {StoppedRunMessage.RerunCommand(command)}";
        if (!managed) return [resume];
        // As with Ctrl+C: deleting the worker loses a CRF search or encode that may still be running there, and a finished
        // encode not yet downloaded.
        var lost = job.ResumeStatus switch
        {
            JobStatus.CrfSearching or JobStatus.Encoding => " (the work in progress is lost)",
            JobStatus.Downloading => " (the finished encode is lost)",
            _ => string.Empty
        };
        return
        [
            "The worker stays up, billed hourly, until it is deleted.",
            resume,
            $"To delete it instead: {StoppedRunMessage.DeleteCommand(command)}{lost}"
        ];
    }
}
