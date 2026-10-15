using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Validate;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Validate;

/// <summary><c>validate &lt;job&gt;</c>: checks a job's AV1 again and records the verdict on the job and in its manifest.</summary>
internal sealed class ValidateCommand(IJobRepository repository, IOutputManifestStore store, IOutputValidationService validation)
{
    public async Task<int> RunAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(command.JobId, out var id)) { WriteError("Job ID is invalid."); return (int)ExitCode.InvalidArguments; }
        var job = await repository.GetAsync(configuration.Settings.Database.Path, id, cancellationToken);
        if (job is null) { WriteError("Job or temporary output was not found."); return (int)ExitCode.ProcessingFailure; }
        // F7 (VOPT-60). A finished job's manifest must not be rewritten, and a failed one cannot become ready again.
        if (!(job.Status is JobStatus.ReadyToFinalize or JobStatus.Validating || job is { Status: JobStatus.Interrupted, ResumeStatus: JobStatus.Validating }))
        {
            WriteError($"validate re-checks a job that is ready to finalise; {job.Id:N} is {job.Status}.");
            return (int)ExitCode.ProcessingFailure;
        }
        if (job.OutputPath is null) { WriteError("Job or temporary output was not found."); return (int)ExitCode.ProcessingFailure; }
        // Validation would fail the job, and cleanup then deletes an AV1 that may be the only copy, so a missing file is
        // reported and the job left as it is.
        var missing = !File.Exists(job.SourcePath) ? FinalizationRules.OriginalMissing(job) : !File.Exists(job.OutputPath) ? FinalizationRules.Av1Missing(job) : null;
        if (missing is not null) { WriteError(missing); return (int)ExitCode.ProcessingFailure; }
        // VOPT-88. Like finalize's F2: without the record validate cannot tell whether the original changed after encoding,
        // and rebuilding it from the job would hide exactly that, so the video is converted again.
        if (await store.TryLoadAsync(job.OutputPath, cancellationToken) is not { } manifest)
        {
            WriteError($"The AV1's validation record is missing or unreadable: {store.GetPath(job.OutputPath)}, so the AV1 can't be checked. To convert the video again, run '{command.Hint($"queue drop {job.Id:N}")}', then '{command.Hint($"queue retry {job.Id:N}")}'.");
            return (int)ExitCode.ProcessingFailure;
        }
        manifest.Validation = await validation.ValidateAsync(manifest, configuration.Settings, cancellationToken);
        // From here the record and the job must agree, so Ctrl+C cannot leave one written without the other.
        await store.SaveAsync(manifest, CancellationToken.None);
        job.ValidationPassed = manifest.Validation.Passed;
        job.SourceSizeBytes = manifest.Validation.SourceSizeBytes;
        job.OutputSizeBytes = manifest.Validation.OutputSizeBytes;
        job.PercentageSaved = manifest.Validation.PercentageSaved;
        job.Status = manifest.Validation.Passed ? JobStatus.ReadyToFinalize : JobStatus.Failed;
        job.ResumeStatus = null;
        if (manifest.Validation.Passed) { job.FailureCategory = null; job.FailureMessage = null; }
        else { job.FailureCategory = "ValidationFailed"; job.FailureMessage = string.Join("; ", manifest.Validation.Failures); job.CompletedUtc = DateTimeOffset.UtcNow; }
        await repository.UpdateAsync(configuration.Settings.Database.Path, job, CancellationToken.None);
        Console.WriteLine(manifest.Validation.Passed ? $"Validation passed: saved {manifest.Validation.PercentageSaved:F1}%" : "Validation failed: " + string.Join("; ", manifest.Validation.Failures));
        return manifest.Validation.Passed ? (int)ExitCode.Success : (int)ExitCode.ValidationFailure;
    }
}
