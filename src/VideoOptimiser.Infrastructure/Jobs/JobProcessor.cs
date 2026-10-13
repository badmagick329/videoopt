using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.Infrastructure.Jobs;

public sealed class JobProcessor(
    IJobRepository jobs,
    IFileReadinessService readiness,
    Func<string, IMediaProbe> mediaProbeFactory,
    IProcessingSessionFactory sessions,
    IFileFingerprintService fingerprints,
    IOutputManifestStore manifests,
    IOutputValidationService validator) : IJobProcessor
{
    public async Task<JobProcessingResult> ProcessAsync(string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(sourcePath);
        if (!File.Exists(path))
        {
            // An open job left as it is would fail the same way on every run, and keep discovery from queueing the file
            // or asset again once it is back.
            var orphan = await jobs.FindOpenBySourceAsync(databasePath, path, cancellationToken);
            if (orphan is not null && orphan.Status is JobStatus.Queued or JobStatus.Interrupted && orphan.ResumeStatus != JobStatus.Finalizing)
                return await FailAsync(orphan, databasePath, QueueService.SourceMissingCategory, $"Original does not exist: {path}", ExitCode.ProcessingFailure, progress, cancellationToken);
            return FailureWithoutJob(ExitCode.ProcessingFailure, $"Original does not exist: {path}");
        }

        var fingerprint = await fingerprints.CreateAsync(path, cancellationToken);
        var job = await jobs.FindOpenBySourceAsync(databasePath, path, cancellationToken);
        var resumeStatus = job?.ResumeStatus;
        if (job is null)
        {
            var id = Guid.NewGuid();
            var remote = string.Equals(settings.Processing.Mode, "remoteSsh", StringComparison.OrdinalIgnoreCase);
            job = await jobs.CreateAsync(databasePath, new JobRecord
            {
                Id = id,
                SourcePath = path,
                SourceFingerprint = fingerprint,
                Status = JobStatus.Queued,
                ExecutionMode = remote ? "remoteSsh" : "local",
                RemoteHost = remote ? RemoteExecutionIdentity.Host(settings.Processing.RemoteSsh) : null,
                RemoteWorkspace = remote ? $"{settings.Processing.RemoteSsh.WorkingDirectory.TrimEnd('/')}/{id:N}" : null
            }, cancellationToken);
        }
        else if (job.Status == JobStatus.ReadyToFinalize)
        {
            return Outcome(job, ExitCode.Success, $"Job {job.Id:N} is already ready to finalise.", progress);
        }
        else if (FinalizationService.IsInterruptedImmichFinalization(job))
        {
            FinalizationService.ReturnToReady(job, FinalizationService.InterruptedMessage);
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            return Outcome(job, ExitCode.Success, $"Job {job.Id:N} was interrupted during finalisation. Run finalize to resume it.", progress);
        }
        else if (job.Status == JobStatus.Finalizing || job.ResumeStatus == JobStatus.Finalizing)
        {
            return await FailAsync(job, databasePath, "ManualInterventionRequired", "Finalisation was interrupted and requires manual review.", ExitCode.FinalisationFailure, progress, cancellationToken);
        }
        var compatibility = JobCompatibility.Evaluate(job, settings);
        if (compatibility.Status == JobCompatibilityStatus.Incompatible)
        {
            return await FailAsync(job, databasePath, JobCompatibility.RemoteConfigurationChangedCategory, compatibility.Message!, ExitCode.InvalidConfiguration, progress, cancellationToken);
        }
        if (compatibility.Status == JobCompatibilityStatus.Adopted)
        {
            JobCompatibility.Apply(job, compatibility.ExpectedBinding);
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
        }

        if (job.Status == JobStatus.Interrupted)
        {
            if (TemporaryOutputClassifier.RestartsEncode(job))
            {
                DeletePartialOutput(job, progress);
                job.OutputPath = null;
                job.ManifestPath = null;
            }
            else if (resumeStatus == JobStatus.CrfSearching && !string.Equals(job.ExecutionMode, "remoteSsh", StringComparison.OrdinalIgnoreCase))
            {
                job.Crf = null;
            }
            job.Status = JobStatus.Queued;
            job.ResumeStatus = null;
            job.FailureCategory = null;
            job.FailureMessage = null;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
        }

        job.SourceFingerprint = fingerprint;
        var stage = "ProcessingFailed";
        await using var session = sessions.Create(job, settings);
        try
        {
            // An Immich job's original is the staged copy, which lives under immich.stagingDirectory rather than a watch root.
            if (!force && job.SourceKind == JobSourceKind.Immich && !(settings.Immich.StagingDirectory.Length > 0 && IsWithinRoot(path, settings.Immich.StagingDirectory))) return await FailAsync(job, databasePath, "SourceOutsideWatchRoot", "Staged Immich original is outside immich.stagingDirectory. Use --force to bypass this check.", ExitCode.ProcessingFailure, progress, cancellationToken);
            if (!force && job.SourceKind == JobSourceKind.File && !settings.Watch.Roots.Any(root => IsWithinRoot(path, root.Path))) return await FailAsync(job, databasePath, "SourceOutsideWatchRoot", "Original is outside configured watch roots. Use --force to bypass this check.", ExitCode.ProcessingFailure, progress, cancellationToken);
            var info = new FileInfo(path);
            var readinessResult = await readiness.CheckAsync(path, cancellationToken);
            if (!readinessResult.IsReady) return await FailAsync(job, databasePath, "SourceNotReady", $"Original is not ready: {readinessResult.Reason}", ExitCode.ProcessingFailure, progress, cancellationToken);

            var media = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(path, cancellationToken);
            if (!force && !EligibilityEvaluator.IsEligible(info, media, settings.Eligibility, out var eligibilityReason)) return await FailAsync(job, databasePath, "Ineligible", eligibilityReason, ExitCode.NoEligibleFiles, progress, cancellationToken);

            var sampleCount = CrfSampleCountCalculator.Calculate(media.DurationSeconds, settings.Quality.CrfSearch.SampleCount);
            var quality = settings.Quality.WithSampleCount(sampleCount);
            if (session.IsRemote)
            {
                stage = "StagingFailed";
                job.Status = JobStatus.Staging;
                await jobs.UpdateAsync(databasePath, job, cancellationToken);
                await session.StageAsync(path, fingerprint, progress, cancellationToken);
                if (!string.Equals(await fingerprints.CreateAsync(path, cancellationToken), fingerprint, StringComparison.Ordinal))
                {
                    await session.CleanupAsync(cancellationToken);
                    return await FailAsync(job, databasePath, "SourceChanged", "Original changed while it was being uploaded.", ExitCode.ProcessingFailure, progress, cancellationToken);
                }
            }
            CrfPrediction? prediction = null;
            if (job.Crf is null)
            {
                stage = "CrfSearchFailed";
                job.Status = JobStatus.CrfSearching;
                await jobs.UpdateAsync(databasePath, job, cancellationToken);
                progress?.Report(JobProgress.Step($"Finding the best CRF for VMAF {quality.MinimumVmaf.ToString("0.##", CultureInfo.InvariantCulture)} ({quality.CrfSearch.SampleCount.ToString(CultureInfo.InvariantCulture)} test {(quality.CrfSearch.SampleCount == 1 ? "clip" : "clips")} of {quality.CrfSearch.SampleDuration} each)..."));
                var crfResult = await session.SearchCrfAsync(path, quality, Translating(progress, AbAv1Stage.CrfSearch, quality), cancellationToken);
                job.Crf = crfResult.Crf;
                prediction = crfResult.Prediction;
                await jobs.UpdateAsync(databasePath, job, cancellationToken);
                progress?.Report(JobProgress.Step(prediction is null
                    ? $"Chose CRF {crfResult.Crf.ToString(CultureInfo.InvariantCulture)} in {HumanReadableValues.FormatDuration(crfResult.Duration)}."
                    : $"Chose CRF {crfResult.Crf.ToString(CultureInfo.InvariantCulture)} in {HumanReadableValues.FormatDuration(crfResult.Duration)}. Expected output: {prediction.Size} ({prediction.Percent.ToString(CultureInfo.InvariantCulture)}% of the original)."));
            }

            OutputManifest manifest;
            var canReuseOutput = resumeStatus == JobStatus.Validating && job.OutputPath is not null && File.Exists(job.OutputPath) && File.Exists(manifests.GetPath(job.OutputPath));
            if (canReuseOutput)
            {
                manifest = await manifests.LoadAsync(job.OutputPath!, cancellationToken);
            }
            else
            {
                stage = "EncodeFailed";
                job.Status = JobStatus.Encoding;
                var reattachAttempt = session.IsRemote && resumeStatus is JobStatus.Encoding or JobStatus.Downloading && job.Attempt > 0;
                if (!reattachAttempt) job.Attempt++;
                var outputDirectory = Path.Combine(Path.GetDirectoryName(path)!, TemporaryOutputClassifier.FolderName);
                var outputPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(path)}.{job.Id:N}.{job.Attempt}.encoding{Path.GetExtension(path)}");
                job.OutputPath = outputPath;
                job.ManifestPath = manifests.GetPath(outputPath);
                await jobs.UpdateAsync(databasePath, job, cancellationToken);
                Directory.CreateDirectory(outputDirectory);
                var crf = job.Crf.Value.ToString(CultureInfo.InvariantCulture);
                progress?.Report(JobProgress.Step(prediction is null ? $"Encoding at CRF {crf}..." : $"Encoding at CRF {crf}, expected to take about {prediction.Taking}..."));
                var encodeResult = await session.EncodeAsync(path, outputPath, job.Crf.Value, quality, job.Attempt, Translating(progress, AbAv1Stage.Encode, quality), cancellationToken);
                if (session.IsRemote)
                {
                    stage = "DownloadFailed";
                    job.Status = JobStatus.Downloading;
                    await jobs.UpdateAsync(databasePath, job, cancellationToken);
                    await session.RetrieveOutputAsync(outputPath, job.Attempt, progress, cancellationToken);
                }
                manifest = new OutputManifest { SourcePath = path, SourceFingerprint = fingerprint, OutputPath = encodeResult.OutputPath, Crf = job.Crf.Value, CreatedUtc = DateTimeOffset.UtcNow };
                await manifests.SaveAsync(manifest, cancellationToken);
                job.OutputPath = encodeResult.OutputPath;
                job.ManifestPath = manifests.GetPath(encodeResult.OutputPath);
            }

            stage = "ValidationFailed";
            job.Status = JobStatus.Validating;
            job.ResumeStatus = null;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            var decodeTest = string.Equals(settings.Validation.DecodeTest, DecodeTestModes.None, StringComparison.OrdinalIgnoreCase) ? string.Empty : " and decode test";
            progress?.Report(JobProgress.Step($"Checking the output: streams, duration, savings{decodeTest}..."));
            manifest.Validation = await validator.ValidateAsync(manifest, settings, cancellationToken);
            await manifests.SaveAsync(manifest, cancellationToken);
            job.ValidationPassed = manifest.Validation.Passed;
            job.SourceSizeBytes = manifest.Validation.SourceSizeBytes;
            job.OutputSizeBytes = manifest.Validation.OutputSizeBytes;
            job.PercentageSaved = manifest.Validation.PercentageSaved;
            if (session.IsRemote) await session.CleanupAsync(cancellationToken);
            if (!manifest.Validation.Passed)
            {
                return await FailAsync(job, databasePath, "ValidationFailed", string.Join("; ", manifest.Validation.Failures), ExitCode.ValidationFailure, progress, cancellationToken);
            }

            job.Status = JobStatus.ReadyToFinalize;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            // Console output stays ASCII: redirected output and legacy consoles use a code page that drops characters like "→".
            progress?.Report(JobProgress.Step($"Ready to finalise: {HumanReadableValues.FormatSize(job.SourceSizeBytes ?? 0)} -> {HumanReadableValues.FormatSize(job.OutputSizeBytes ?? 0)}, {(job.PercentageSaved ?? 0).ToString("F1", CultureInfo.InvariantCulture)}% saved."));
            return new JobProcessingResult(job, ExitCode.Success, $"Job {job.Id:N} is ready to finalise.");
        }
        catch (OperationCanceledException)
        {
            if (session.IsRemote) await session.CancelAsync(CancellationToken.None);
            job.ResumeStatus = session.IsRemote && job.Status == JobStatus.Encoding ? JobStatus.Queued : job.Status;
            job.Status = JobStatus.Interrupted;
            job.FailureCategory = "Interrupted";
            job.FailureMessage = "Processing was cancelled.";
            await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
            throw;
        }
        catch (NoWorthwhileCrfException exception)
        {
            return await SkipAsync(job, databasePath, settings, exception, path, progress, cancellationToken);
        }
        catch (RemoteConnectionException exception)
        {
            job.ResumeStatus = job.Status;
            job.Status = JobStatus.Interrupted;
            job.FailureCategory = "RemoteUnavailable";
            job.FailureMessage = exception.Message;
            await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
            progress?.Report(JobProgress.Step($"Worker unreachable: {exception.Message.TrimEnd('.')}. Run the same command again to resume."));
            return new JobProcessingResult(job, ExitCode.ProcessingFailure, exception.Message);
        }
        catch (Exception exception)
        {
            return await FailAsync(job, databasePath, stage, exception.Message, ExitCode.ProcessingFailure, progress, cancellationToken, (exception as AbAv1StageException)?.LogPath);
        }
    }

    /// <summary>Each ab-av1 stage gets a fresh translator: its per-stage state (predictions, progress throttle, timing) must not carry over.</summary>
    private static IProgress<JobProgress>? Translating(IProgress<JobProgress>? progress, AbAv1Stage stage, QualitySettings quality) =>
        progress is null ? null : new AbAv1OutputTranslator(stage, quality.MinimumVmaf, quality.MaxEncodedPercent).Wrap(progress);

    /// <summary>A result for a job that reached no stage: its message is already the job's outcome, so it is reported as the last line.</summary>
    private static JobProcessingResult Outcome(JobRecord job, ExitCode exitCode, string message, IProgress<JobProgress>? progress)
    {
        progress?.Report(JobProgress.Step(message));
        return new JobProcessingResult(job, exitCode, message);
    }

    /// <summary>
    /// Matches by job ID and attempt rather than by <see cref="JobRecord.OutputPath"/>, because ab-av1 writes the encode
    /// under a temporary name of its own and renames it only on success. A failed delete is reported, not fatal: the new
    /// attempt writes a different file, and <c>cleanup</c> can remove this one later, for example once an ab-av1 orphaned
    /// by a crash releases it.
    /// </summary>
    private static void DeletePartialOutput(JobRecord job, IProgress<JobProgress>? progress)
    {
        var folder = Path.Combine(Path.GetDirectoryName(job.SourcePath)!, TemporaryOutputClassifier.FolderName);
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (!TemporaryOutputClassifier.TryParse(Path.GetFileName(file), out var jobId, out var attempt) || jobId != job.Id || attempt != job.Attempt) continue;
            try
            {
                File.Delete(file);
                progress?.Report(JobProgress.Step($"Deleted the partial output of interrupted attempt {job.Attempt}."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                progress?.Report(JobProgress.Step($"Could not delete the partial output of attempt {job.Attempt} ({exception.Message}); run 'cleanup' later to remove {Path.GetFileName(file)}."));
            }
        }
    }

    private static JobProcessingResult FailureWithoutJob(ExitCode exitCode, string message) => new(new JobRecord { Status = JobStatus.Failed }, exitCode, message);

    /// <param name="logPath">The kept ab-av1 log of the stage that failed. It is named in the job's failure message too, so
    /// <c>history</c> shows where to look after the console output has scrolled away.</param>
    private async Task<JobProcessingResult> FailAsync(JobRecord job, string databasePath, string category, string message, ExitCode exitCode, IProgress<JobProgress>? progress, CancellationToken cancellationToken, string? logPath = null)
    {
        var stored = logPath is null ? message : $"{message} Full log: {logPath}";
        job.Status = JobStatus.Failed;
        job.FailureCategory = category;
        job.FailureMessage = stored;
        job.CompletedUtc = DateTimeOffset.UtcNow;
        await jobs.UpdateAsync(databasePath, job, cancellationToken);
        progress?.Report(JobProgress.Step(category switch
        {
            "ValidationFailed" => $"Failed validation: {message}",
            "StagingFailed" => $"Failed during the upload: {message}",
            "CrfSearchFailed" => $"Failed during the CRF search: {message}",
            "EncodeFailed" => $"Failed during encoding: {message}",
            "DownloadFailed" => $"Failed during the download: {message}",
            _ => $"Failed: {message}"
        }));
        if (logPath is not null) progress?.Report(JobProgress.Step($"Full log: {logPath}"));
        return new JobProcessingResult(job, exitCode, stored);
    }

    /// <summary>
    /// Ends the job as skipped: AV1 cannot meet the quality bar and the size limit together. The fingerprint lets discovery
    /// check the video again once those settings change. The remote stage has already cleaned its workspace on the way out.
    /// </summary>
    private async Task<JobProcessingResult> SkipAsync(JobRecord job, string databasePath, AppSettings settings, NoWorthwhileCrfException exception, string path, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Skipped;
        job.FailureCategory = NotWorthConvertingCategory;
        job.FailureMessage = exception.Message;
        job.QualityFingerprint = QualityFingerprint.Compute(settings.Quality);
        job.CompletedUtc = DateTimeOffset.UtcNow;
        await jobs.UpdateAsync(databasePath, job, cancellationToken);
        progress?.Report(JobProgress.Step($"Not worth converting: {exception.Message}"));
        return new JobProcessingResult(job, ExitCode.Success, exception.Message);
    }

    internal const string NotWorthConvertingCategory = "NotWorthConverting";

    private static bool IsWithinRoot(string sourcePath, string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
        return sourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
