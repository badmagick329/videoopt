using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;

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
    public async Task<JobProcessingResult> ProcessAsync(string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(sourcePath);
        if (!File.Exists(path)) return FailureWithoutJob(ExitCode.ProcessingFailure, $"Source file does not exist: {path}");

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
            return new JobProcessingResult(job, ExitCode.Success, $"Job {job.Id:N} is already ready to finalize.");
        }
        else if (job.Status == JobStatus.Finalizing || job.ResumeStatus == JobStatus.Finalizing)
        {
            return await FailAsync(job, databasePath, "ManualInterventionRequired", "Finalisation was interrupted and requires manual review.", ExitCode.FinalisationFailure, cancellationToken);
        }
        else if (job.Status == JobStatus.Interrupted)
        {
            if (!string.Equals(job.ExecutionMode, "remoteSsh", StringComparison.OrdinalIgnoreCase))
            {
                if (resumeStatus == JobStatus.CrfSearching) job.Crf = null;
                if (resumeStatus == JobStatus.Encoding) { job.OutputPath = null; job.ManifestPath = null; }
            }
            job.Status = JobStatus.Queued;
            job.ResumeStatus = null;
            job.FailureCategory = null;
            job.FailureMessage = null;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
        }

        var activeMode = string.Equals(settings.Processing.Mode, "remoteSsh", StringComparison.OrdinalIgnoreCase) ? "remoteSsh" : "local";
        var activeHost = activeMode == "remoteSsh" ? RemoteExecutionIdentity.Host(settings.Processing.RemoteSsh) : null;
        var activeWorkspace = activeMode == "remoteSsh" ? $"{settings.Processing.RemoteSsh.WorkingDirectory.TrimEnd('/')}/{job.Id:N}" : null;
        if (!string.Equals(job.ExecutionMode, activeMode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(job.RemoteHost, activeHost, StringComparison.Ordinal) ||
            !string.Equals(job.RemoteWorkspace, activeWorkspace, StringComparison.Ordinal))
        {
            return await FailAsync(job, databasePath, "RemoteConfigurationChanged", "The processing mode, remote host, or remote workspace differs from the configuration captured for this job.", ExitCode.InvalidConfiguration, cancellationToken);
        }

        job.SourceFingerprint = fingerprint;
        var stage = "ProcessingFailed";
        await using var session = sessions.Create(job, settings);
        try
        {
            if (!force && !settings.Watch.Roots.Any(root => IsWithinRoot(path, root.Path))) return await FailAsync(job, databasePath, "SourceOutsideWatchRoot", "Source file is outside configured watch roots. Use --force to bypass this check.", ExitCode.ProcessingFailure, cancellationToken);
            var info = new FileInfo(path);
            var readinessResult = await readiness.CheckAsync(path, cancellationToken);
            if (!readinessResult.IsReady) return await FailAsync(job, databasePath, "SourceNotReady", $"Source is not ready: {readinessResult.Reason}", ExitCode.ProcessingFailure, cancellationToken);

            var media = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(path, cancellationToken);
            if (!force && !EligibilityEvaluator.IsEligible(info, media, settings.Eligibility, out var eligibilityReason)) return await FailAsync(job, databasePath, "Ineligible", eligibilityReason, ExitCode.NoEligibleFiles, cancellationToken);

            var sampleCount = CrfSampleCountCalculator.Calculate(media.DurationSeconds, settings.Quality.CrfSearch.SampleCount);
            var quality = WithSampleCount(settings.Quality, sampleCount);
            if (session.IsRemote)
            {
                stage = "StagingFailed";
                job.Status = JobStatus.Staging;
                await jobs.UpdateAsync(databasePath, job, cancellationToken);
                progress?.Report(new CrfSearchOutput("staging", $"Staging: preparing {Path.GetFileName(path)} for remote processing."));
                await session.StageAsync(path, fingerprint, progress, cancellationToken);
                progress?.Report(new CrfSearchOutput("staging", "Staging: confirming the local source did not change during upload."));
                if (!string.Equals(await fingerprints.CreateAsync(path, cancellationToken), fingerprint, StringComparison.Ordinal))
                {
                    await session.CleanupAsync(cancellationToken);
                    return await FailAsync(job, databasePath, "SourceChanged", "Source file changed while it was being uploaded.", ExitCode.ProcessingFailure, cancellationToken);
                }
                progress?.Report(new CrfSearchOutput("staging", "Staging: source is ready on the remote worker."));
            }
            if (job.Crf is null)
            {
                stage = "CrfSearchFailed";
                job.Status = JobStatus.CrfSearching;
                await jobs.UpdateAsync(databasePath, job, cancellationToken);
                var crfResult = await session.SearchCrfAsync(path, quality, progress, cancellationToken);
                job.Crf = crfResult.Crf;
                await jobs.UpdateAsync(databasePath, job, cancellationToken);
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
                var outputDirectory = Path.Combine(Path.GetDirectoryName(path)!, ".video-optimiser");
                var outputPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(path)}.{job.Id:N}.{job.Attempt}.encoding{Path.GetExtension(path)}");
                job.OutputPath = outputPath;
                job.ManifestPath = manifests.GetPath(outputPath);
                await jobs.UpdateAsync(databasePath, job, cancellationToken);
                Directory.CreateDirectory(outputDirectory);
                var encodeResult = await session.EncodeAsync(path, outputPath, job.Crf.Value, quality, job.Attempt, progress, cancellationToken);
                if (session.IsRemote)
                {
                    stage = "DownloadFailed";
                    job.Status = JobStatus.Downloading;
                    await jobs.UpdateAsync(databasePath, job, cancellationToken);
                    progress?.Report(new CrfSearchOutput("downloading", $"Downloading: retrieving {Path.GetFileName(outputPath)} from the remote worker."));
                    await session.RetrieveOutputAsync(outputPath, job.Attempt, progress, cancellationToken);
                    progress?.Report(new CrfSearchOutput("downloading", "Downloading: output retrieved and verified."));
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
            manifest.Validation = await validator.ValidateAsync(manifest, settings, cancellationToken);
            await manifests.SaveAsync(manifest, cancellationToken);
            job.ValidationPassed = manifest.Validation.Passed;
            job.SourceSizeBytes = manifest.Validation.SourceSizeBytes;
            job.OutputSizeBytes = manifest.Validation.OutputSizeBytes;
            job.PercentageSaved = manifest.Validation.PercentageSaved;
            if (session.IsRemote) await session.CleanupAsync(cancellationToken);
            if (!manifest.Validation.Passed)
            {
                return await FailAsync(job, databasePath, "ValidationFailed", string.Join("; ", manifest.Validation.Failures), ExitCode.ValidationFailure, cancellationToken);
            }

            job.Status = JobStatus.ReadyToFinalize;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            return new JobProcessingResult(job, ExitCode.Success, $"Job {job.Id:N} is ready to finalize.");
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
        catch (RemoteConnectionException exception)
        {
            job.ResumeStatus = job.Status;
            job.Status = JobStatus.Interrupted;
            job.FailureCategory = "RemoteUnavailable";
            job.FailureMessage = exception.Message;
            await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
            return new JobProcessingResult(job, ExitCode.ProcessingFailure, exception.Message);
        }
        catch (Exception exception)
        {
            return await FailAsync(job, databasePath, stage, exception.Message, ExitCode.ProcessingFailure, cancellationToken);
        }
    }

    private static JobProcessingResult FailureWithoutJob(ExitCode exitCode, string message) => new(new JobRecord { Status = JobStatus.Failed }, exitCode, message);

    private async Task<JobProcessingResult> FailAsync(JobRecord job, string databasePath, string category, string message, ExitCode exitCode, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Failed;
        job.FailureCategory = category;
        job.FailureMessage = message;
        job.CompletedUtc = DateTimeOffset.UtcNow;
        await jobs.UpdateAsync(databasePath, job, cancellationToken);
        return new JobProcessingResult(job, exitCode, message);
    }

    private static bool IsWithinRoot(string sourcePath, string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
        return sourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static QualitySettings WithSampleCount(QualitySettings source, int sampleCount) => new()
    {
        MinimumVmaf = source.MinimumVmaf,
        Preset = source.Preset,
        Encoder = source.Encoder,
        PixelFormat = source.PixelFormat,
        CrfSearch = new CrfSearchSettings { Enabled = source.CrfSearch.Enabled, MinCrf = source.CrfSearch.MinCrf, MaxCrf = source.CrfSearch.MaxCrf, SampleCount = sampleCount, SampleDuration = source.CrfSearch.SampleDuration }
    };

}
