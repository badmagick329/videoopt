using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Validate;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// Checks again, at the start of a run and without encoding, the videos that failed validation under older rules (VOPT-119).
/// Every validation failure the user hit was a false alarm on phone video (VOPT-116), and a failed job keeps its AV1 on disk,
/// so the finished encode is rescued rather than redone. The user chose that this happens automatically. It needs no worker:
/// validation is local, and the verdict is recorded as <c>validate</c> records it.
/// </summary>
/// <remarks>
/// A job qualifies only when its AV1 and record are on disk, the report in the record predates
/// <see cref="ValidationReport.CurrentRulesVersion"/> (a video that fails under the current rules is not checked again every
/// run), and its original is unchanged since the encode, by the same fingerprint comparison finalisation makes. Source kind
/// does not matter: every run prepares the whole queue. An Immich job whose staged copy is gone fails the original's existence
/// check and is left alone; this never downloads it again. A job that does not qualify is left as it is, and nothing is raised.
/// </remarks>
public sealed class FailedValidationRecheck(IJobRepository jobs, IOutputManifestStore manifests, IFileFingerprintService fingerprints, IOutputValidationService validator)
{
    public async Task RecheckAsync(AppSettings settings, IRunObserver observer, CancellationToken cancellationToken)
    {
        var db = settings.Database.Path;
        var candidates = await SelectAsync(db, cancellationToken);
        if (candidates.Count == 0) return;

        var progress = observer.RecheckStarted(candidates.Count);
        int ready = 0, toCheck = 0, notWorthConverting = 0, stillFailed = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            var (job, manifest) = candidates[index];
            progress.Report(JobProgress.Header(index + 1, candidates.Count, job.SourcePath));
            progress.Report(JobValidation.CheckingStep(settings));
            try
            {
                manifest.Validation = await validator.ValidateAsync(manifest, settings, cancellationToken);
            }
            catch (Exception exception) when (exception is MediaProbeException or IOException or UnauthorizedAccessException)
            {
                progress.Report(JobProgress.Outcome(JobTone.Failure, $"Couldn't check it again: {exception.Message}"));
                stillFailed++;
                continue;
            }

            // From the verdict on, no token (as in ValidateCommand): Ctrl+C must not save the record without the job.
            var verdict = await JobValidation.RecordAsync(job, manifest, settings, manifests);
            try
            {
                await jobs.UpdateAsync(db, job, CancellationToken.None);
            }
            catch (JobChangedException)
            {
                // Another terminal changed the job meanwhile; its change stands.
                stillFailed++;
                continue;
            }

            foreach (var line in JobValidation.Describe(job, verdict)) progress.Report(line);
            switch (verdict)
            {
                case ValidationVerdict.Failed:
                    stillFailed++;
                    break;
                case ValidationVerdict.NotWorthConverting:
                    notWorthConverting++;
                    break;
                default:
                    ready++;
                    if (job.ValidationWarnings.Count > 0) toCheck++;
                    break;
            }
        }
        observer.Rechecked(ready, toCheck, notWorthConverting, stillFailed);
    }

    private async Task<List<(JobRecord Job, OutputManifest Manifest)>> SelectAsync(string db, CancellationToken cancellationToken)
    {
        var all = (await jobs.ListAsync(db, terminal: true, cancellationToken)).Concat(await jobs.ListAsync(db, terminal: false, cancellationToken)).ToList();
        var selected = new List<(JobRecord, OutputManifest)>();
        foreach (var job in all)
        {
            if (job is not { Status: JobStatus.Failed, FailureCategory: FailureCategories.ValidationFailed, OutputPath: { } outputPath }) continue;
            // A later job for the same original (a retry or a new discovery) owns it now; the same rule as the TUI's failed list.
            if (all.Any(other => other.CreatedUtc > job.CreatedUtc && (string.Equals(other.SourcePath, job.SourcePath, StringComparison.OrdinalIgnoreCase) || job.ImmichAssetId is not null && other.ImmichAssetId == job.ImmichAssetId))) continue;
            if (!File.Exists(outputPath)) continue;
            var manifest = await manifests.TryLoadAsync(outputPath, cancellationToken);
            if (manifest is null || (manifest.Validation?.RulesVersion ?? 0) >= ValidationReport.CurrentRulesVersion) continue;
            if (!File.Exists(manifest.SourcePath)) continue;
            if (!await OriginalUnchangedAsync(manifest, cancellationToken)) continue;
            selected.Add((job, manifest));
        }
        return selected;
    }

    /// <summary>An original that can't be read right now (open in another program) is left for a later run, not an error that stops this one.</summary>
    private async Task<bool> OriginalUnchangedAsync(OutputManifest manifest, CancellationToken cancellationToken)
    {
        try
        {
            return await fingerprints.CreateAsync(manifest.SourcePath, cancellationToken) == manifest.SourceFingerprint;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
