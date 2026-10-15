namespace VideoOptimiser.Domain.Jobs;

/// <summary>
/// A write lost to a change made since the job was read: another process wrote it (a queue verb, a run), or it is held. The
/// writer passes over the job, or tells the user to try again; it never overwrites the change.
/// </summary>
public sealed class JobChangedException(Guid jobId) : InvalidOperationException($"Job {jobId:N} changed while this command was using it. Run the command again.")
{
    public Guid JobId { get; } = jobId;
}

/// <summary>A new job for an original that already has an open one. Callers check first; this catches the race between two commands.</summary>
public sealed class OpenJobExistsException(string sourcePath) : InvalidOperationException($"An open job already exists for {sourcePath}.")
{
    public string SourcePath { get; } = sourcePath;
}

/// <summary>
/// Ctrl+C stopped a job. It carries the job and the stage it stopped in, so the CLI can say what is still running on the
/// worker: a run of several jobs cannot tell which <see cref="JobStatus.Interrupted"/> job was the one in progress.
/// </summary>
public sealed class JobInterruptedException(JobRecord job, JobStatus stage, bool remote, CancellationToken cancellationToken)
    : OperationCanceledException("Stopped before it finished.", cancellationToken)
{
    public JobRecord Job { get; } = job;
    public JobStatus Stage { get; } = stage;
    public bool Remote { get; } = remote;
}
