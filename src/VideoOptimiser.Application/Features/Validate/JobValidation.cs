using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Validate;

/// <summary>
/// Puts a validation verdict on a job and its files the same way wherever validation runs: in a run, in <c>validate</c>,
/// and when a run checks again a job that failed validation under the old rules (VOPT-116). The caller writes the job.
/// </summary>
public static class JobValidation
{
    /// <summary>
    /// Saves <paramref name="manifest"/> with its report, or, when the AV1 is not worth converting, deletes the AV1 and its
    /// record; then sets the job's status and validation fields for the verdict. Takes no token: once a verdict exists, the
    /// record and the job must agree, so the caller writes the job with <see cref="CancellationToken.None"/> as well.
    /// </summary>
    public static async Task<ValidationVerdict> RecordAsync(JobRecord job, OutputManifest manifest, AppSettings settings, IOutputManifestStore store)
    {
        var report = manifest.Validation ?? throw new InvalidOperationException("The manifest has no validation report to record.");
        report.RulesVersion = ValidationReport.CurrentRulesVersion;
        var verdict = report.Verdict;
        if (verdict == ValidationVerdict.NotWorthConverting) DeleteAv1(manifest.OutputPath, store);
        else await store.SaveAsync(manifest, CancellationToken.None);

        job.ValidationPassed = report.Passed;
        job.SourceSizeBytes = report.SourceSizeBytes;
        job.OutputSizeBytes = report.OutputSizeBytes;
        job.PercentageSaved = report.PercentageSaved;
        job.ValidationWarnings = verdict == ValidationVerdict.Warned ? [.. report.Warnings] : [];
        job.ResumeStatus = null;
        switch (verdict)
        {
            case ValidationVerdict.Passed or ValidationVerdict.Warned:
                job.Status = JobStatus.ReadyToFinalize;
                job.FailureCategory = null;
                job.FailureMessage = null;
                job.CompletedUtc = null;
                break;
            case ValidationVerdict.NotWorthConverting:
                job.Status = JobStatus.Skipped;
                job.FailureCategory = FailureCategories.NotWorthConverting;
                job.FailureMessage = report.NotWorthConverting;
                // As a CRF search's skip records it, so discovery checks the video again once these settings change.
                job.QualityFingerprint = QualityFingerprint.Compute(settings);
                job.CompletedUtc = DateTimeOffset.UtcNow;
                break;
            default:
                job.Status = JobStatus.Failed;
                job.FailureCategory = FailureCategories.ValidationFailed;
                job.FailureMessage = string.Join("; ", report.Failures);
                job.CompletedUtc = DateTimeOffset.UtcNow;
                break;
        }
        return verdict;
    }

    /// <summary>The step line shown before validating, so a run and a re-check word it alike.</summary>
    public static JobProgress CheckingStep(AppSettings settings)
    {
        var decodeTest = string.Equals(settings.Validation.DecodeTest, DecodeTestModes.None, StringComparison.OrdinalIgnoreCase) ? string.Empty : " and decode test";
        return JobProgress.Step($"Checking the output: streams, duration, savings{decodeTest}.");
    }

    /// <summary>
    /// The lines a run shows for a recorded verdict: the outcome, then each warning on its own line in the warning tone, so
    /// the console colours it and the TUI logs it as a warning. A ready video's notes follow in the plain tone: they ask
    /// nothing of the person (VOPT-125).
    /// </summary>
    public static IEnumerable<JobProgress> Describe(JobRecord job, ValidationVerdict verdict)
    {
        switch (verdict)
        {
            case ValidationVerdict.Failed:
                yield return JobProgress.Outcome(JobTone.Failure, $"Failed validation: {job.FailureMessage}");
                yield break;
            case ValidationVerdict.NotWorthConverting:
                yield return JobProgress.Outcome(JobTone.Warning, $"Not worth converting: {job.FailureMessage}");
                yield break;
        }
        // Console output stays ASCII: redirected output and legacy consoles use a code page that drops characters like "→".
        var sizes = $"{HumanReadableValues.FormatSize(job.SourceSizeBytes ?? 0)} -> {HumanReadableValues.FormatSize(job.OutputSizeBytes ?? 0)}, {(job.PercentageSaved ?? 0).ToString("F1", CultureInfo.InvariantCulture)}% saved.";
        if (job.ValidationWarnings.Count == 0)
        {
            yield return JobProgress.Outcome(JobTone.Success, $"Ready to finalise: {sizes}");
        }
        else
        {
            yield return JobProgress.Outcome(JobTone.Success, $"Ready to finalise: {sizes} Check it before finalising:");
            foreach (var warning in job.ValidationWarnings) yield return JobProgress.Detail(warning, JobTone.Warning);
        }
        foreach (var note in job.Notes) yield return JobProgress.Detail($"Note: {note}");
    }

    /// <summary>
    /// Best effort: the job ends skipped either way, and an AV1 left behind (say, open in a player) belongs to an ended job,
    /// which <c>cleanup</c> removes.
    /// </summary>
    private static void DeleteAv1(string outputPath, IOutputManifestStore store)
    {
        foreach (var path in new[] { outputPath, store.GetPath(outputPath) })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
