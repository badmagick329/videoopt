using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// Removing a job's workspace on the worker once the job is done with it. It is housekeeping: the sweep of finished jobs'
/// workspaces (<see cref="IProcessingSession.StopAndRemoveWorkspacesAsync"/>) removes whatever is left on a later run, so a
/// removal that fails must never change the job's verdict or hide the error that ended it (VOPT-157).
/// </summary>
public static class WorkspaceCleanup
{
    /// <summary>
    /// Runs <paramref name="cleanup"/> and swallows anything it throws but <see cref="OutOfMemoryException"/>, saying so in one
    /// dim line. A worker that stopped answering is swallowed too: the job's own outcome is already settled, and the next job's
    /// stage meets the same worker and reports it properly.
    /// <para>A stop (<paramref name="cancellationToken"/> cancelled) is swallowed without a line. Callers decide the job's
    /// outcome before this runs, and after validation have already written it, so letting the stop through would only record a
    /// job that had finished as stopped, to be checked again; the run notices the stop before it starts the next job.</para>
    /// </summary>
    public static async Task TryAsync(Func<CancellationToken, Task> cleanup, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            await cleanup(cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                progress?.Report(JobProgress.Detail($"Couldn't clear this video's workspace on the worker ({exception.Message.TrimEnd('.')}); a later run will."));
            }
        }
    }
}
