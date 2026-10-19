using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain.Jobs;

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

/// <summary>How a skipped started job can be resumed; see <see cref="JobCompatibility.AdviseSkipped"/>.</summary>
public enum SkippedJobResume
{
    /// <summary>The config matches the job: the same run without <c>--local</c> or <c>--remote</c>.</summary>
    RunWithoutFlag,
    /// <summary>The config's mode differs from the job's, and the flag that names the job's mode matches its worker.</summary>
    RunWithFlag,
    /// <summary>The config's worker differs from the job's in either mode: change the config, or drop the job.</summary>
    ChangeConfig,
    /// <summary>The config names the job's worker but another working directory: change that setting back, or drop the job.</summary>
    ChangeWorkingDirectory
}

/// <param name="JobWhere">Where the job started, such as <c>this PC</c> or <c>the Hetzner worker</c>.</param>
/// <param name="ConfigWhere">Where the config sends work: its mode, or for <see cref="SkippedJobResume.ChangeConfig"/> the name of its worker.</param>
/// <param name="JobMode">The job's own mode, which names the flag for <see cref="SkippedJobResume.RunWithFlag"/>.</param>
public sealed record SkippedJobAdvice(SkippedJobResume Resume, string JobWhere, string ConfigWhere, string JobMode);

public static class JobCompatibility
{
    public const string RemoteConfigurationChangedMessage = "The processing mode, remote host, or remote workspace differs from the configuration captured for this job.";

    /// <param name="mode">The mode to judge the job under instead of <c>processing.mode</c>; see <see cref="AdviseSkipped"/>.</param>
    public static JobCompatibilityResult Evaluate(JobRecord job, AppSettings settings, string? mode = null)
    {
        var expected = ExpectedBinding(job.Id, settings, mode);
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

    /// <summary>
    /// What would resume a started job that <c>--local</c> or <c>--remote</c> made a run skip (VOPT-74). The flag only changes
    /// the mode, so a plain run resumes the job only if the config, as the file says it, matches the job in mode and worker.
    /// Otherwise the job's own mode may match the config's worker block, and a run with that flag resumes it; when even that
    /// fails, the config points at another worker and only changing it, or dropping the job, helps.
    /// </summary>
    /// <param name="configuredMode"><c>processing.mode</c> as the file says it, before the flag replaced it.</param>
    public static SkippedJobAdvice AdviseSkipped(JobRecord job, AppSettings settings, string configuredMode)
    {
        var jobMode = string.Equals(job.ExecutionMode, ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase) ? ProcessingModes.RemoteSsh : ProcessingModes.Local;
        var jobWhere = Describe(job.ExecutionMode, job.RemoteHost);
        var configured = ExpectedBinding(job.Id, settings, configuredMode);
        var configuredWhere = Describe(configured.ExecutionMode, configured.RemoteIdentity);
        if (Evaluate(job, settings, configuredMode).Status != JobCompatibilityStatus.Incompatible) return new SkippedJobAdvice(SkippedJobResume.RunWithoutFlag, jobWhere, configuredWhere, jobMode);
        if (Evaluate(job, settings, jobMode).Status != JobCompatibilityStatus.Incompatible) return new SkippedJobAdvice(SkippedJobResume.RunWithFlag, jobWhere, configuredWhere, jobMode);
        // Only a remote job can get here: a job that started on this PC matches whatever the remote block says.
        var worker = ExpectedBinding(job.Id, settings, ProcessingModes.RemoteSsh);
        // The same worker under another working directory: the host names do not tell the two apart, the setting does.
        if (RemoteExecutionIdentity.Matches(job.RemoteHost, worker.RemoteIdentity)) return new SkippedJobAdvice(SkippedJobResume.ChangeWorkingDirectory, jobWhere, jobWhere, jobMode);
        return new SkippedJobAdvice(SkippedJobResume.ChangeConfig, jobWhere, Describe(worker.ExecutionMode, worker.RemoteIdentity, nameOnly: true), jobMode);
    }

    /// <param name="nameOnly">The worker's name alone, for a sentence that already says it is a worker.</param>
    private static string Describe(string? executionMode, string? remoteIdentity, bool nameOnly = false) =>
        !string.Equals(executionMode, ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase) ? "this PC"
        : RemoteExecutionIdentity.IsManagedHetzner(remoteIdentity) ? "the Hetzner worker"
        : nameOnly ? remoteIdentity ?? string.Empty
        : $"the worker {remoteIdentity}";

    /// <param name="mode">The mode to bind to instead of <c>processing.mode</c>, to ask what the binding would be under another.</param>
    public static JobExecutionBinding ExpectedBinding(Guid jobId, AppSettings settings, string? mode = null)
    {
        if (!string.Equals(mode ?? settings.Processing.Mode, ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase))
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
