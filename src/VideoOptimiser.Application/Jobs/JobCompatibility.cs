using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Jobs;

public enum JobCompatibilityStatus
{
    Compatible,
    Adopted,
    Incompatible
}

public sealed record JobExecutionBinding(string ExecutionMode, string? RemoteIdentity, string? RemoteWorkspace);

public sealed record JobCompatibilityResult(JobCompatibilityStatus Status, JobExecutionBinding ExpectedBinding, string? Message = null)
{
    public bool CanRun => Status is JobCompatibilityStatus.Compatible or JobCompatibilityStatus.Adopted;
}

public static class JobCompatibility
{
    public const string RemoteConfigurationChangedCategory = "RemoteConfigurationChanged";
    public const string RemoteConfigurationChangedMessage = "The processing mode, remote host, or remote workspace differs from the configuration captured for this job.";

    public static JobCompatibilityResult Evaluate(JobRecord job, AppSettings settings)
    {
        var expected = ExpectedBinding(job.Id, settings);
        if (IsNeverStarted(job))
        {
            return Matches(job, expected)
                ? new JobCompatibilityResult(JobCompatibilityStatus.Compatible, expected)
                : new JobCompatibilityResult(JobCompatibilityStatus.Adopted, expected);
        }

        return Matches(job, expected)
            ? new JobCompatibilityResult(JobCompatibilityStatus.Compatible, expected)
            : new JobCompatibilityResult(JobCompatibilityStatus.Incompatible, expected, RemoteConfigurationChangedMessage);
    }

    /// <summary>
    /// A started job that the current processing mode cannot resume because it ran in the other mode. Under <c>--local</c> or
    /// <c>--remote</c> such a job is skipped rather than failed: the override lasts one run, and failing the job would throw away
    /// its CRF search or a retained worker's encode. A started job in the same mode on a changed host or workspace is a real
    /// configuration change and still fails. A job past processing, such as one ready to finalise, is not waiting to resume.
    /// </summary>
    public static bool IsBoundToOtherMode(JobRecord job, JobCompatibilityResult result) =>
        job.Status is JobStatus.Queued or JobStatus.Interrupted &&
        result.Status == JobCompatibilityStatus.Incompatible &&
        !string.Equals(job.ExecutionMode, result.ExpectedBinding.ExecutionMode, StringComparison.OrdinalIgnoreCase);

    public static string SkippedMessage(JobRecord job) =>
        $"Skipped {Path.GetFileName(job.SourcePath)}: started on {DescribeBinding(job)}; run without --local/--remote to resume it.";

    private static string DescribeBinding(JobRecord job) =>
        !string.Equals(job.ExecutionMode, ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase) ? "this PC"
        : RemoteExecutionIdentity.IsManagedHetzner(job.RemoteHost) ? "a managed Hetzner server"
        : $"{job.RemoteHost} over SSH";

    public static JobExecutionBinding ExpectedBinding(Guid jobId, AppSettings settings)
    {
        if (!string.Equals(settings.Processing.Mode, ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase))
        {
            return new JobExecutionBinding(ProcessingModes.Local, null, null);
        }

        return new JobExecutionBinding(
            ProcessingModes.RemoteSsh,
            RemoteExecutionIdentity.Host(settings.Processing.RemoteSsh),
            $"{settings.Processing.RemoteSsh.WorkingDirectory.TrimEnd('/')}/{jobId:N}");
    }

    public static bool IsNeverStarted(JobRecord job) =>
        job.Status == JobStatus.Queued &&
        job.ResumeStatus is null &&
        job.Crf is null &&
        job.Attempt == 0 &&
        job.OutputPath is null &&
        job.ManifestPath is null &&
        job.ValidationPassed is null &&
        job.SourceSizeBytes is null &&
        job.OutputSizeBytes is null &&
        job.PercentageSaved is null;

    public static void Apply(JobRecord job, JobExecutionBinding binding)
    {
        job.ExecutionMode = binding.ExecutionMode;
        job.RemoteHost = binding.RemoteIdentity;
        job.RemoteWorkspace = binding.RemoteWorkspace;
    }

    private static bool Matches(JobRecord job, JobExecutionBinding binding) =>
        string.Equals(job.ExecutionMode, binding.ExecutionMode, StringComparison.OrdinalIgnoreCase) &&
        RemoteExecutionIdentity.Matches(job.RemoteHost, binding.RemoteIdentity) &&
        string.Equals(job.RemoteWorkspace, binding.RemoteWorkspace, StringComparison.Ordinal);
}
