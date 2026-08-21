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
        string.Equals(job.RemoteHost, binding.RemoteIdentity, StringComparison.Ordinal) &&
        string.Equals(job.RemoteWorkspace, binding.RemoteWorkspace, StringComparison.Ordinal);
}
