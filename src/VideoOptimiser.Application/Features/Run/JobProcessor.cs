using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.Validate;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Run;

public sealed class JobProcessor(
    IJobRepository jobs,
    IFileReadinessService readiness,
    Func<string, IMediaProbe> mediaProbeFactory,
    IProcessingSessionFactory sessions,
    IFileFingerprintService fingerprints,
    IOutputManifestStore manifests,
    IOutputValidationService validator,
    IAbAv1ProgressTranslator translator,
    ICommandHints hints) : IJobProcessor
{
    public async Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(job.SourcePath);
        // Left queued, the job would fail the same way on every run, and keep discovery from queueing the file or asset
        // again once it is back.
        if (!File.Exists(path)) return await FailAsync(job, databasePath, FailureCategories.SourceMissing, $"Original does not exist: {path}", ExitCode.ProcessingFailure, progress, cancellationToken);

        var resumeStatus = job.ResumeStatus;
        var force = job.Forced;
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

        var stage = "ProcessingFailed";
        await using var session = sessions.Create(job, settings);
        try
        {
            // Read inside the try, with the other reads of the original: a file that vanishes after the exists check above, or
            // cannot be read, fails this job like any other, rather than ending the run as a configuration error.
            string fingerprint;
            try
            {
                fingerprint = await fingerprints.CreateAsync(path, cancellationToken);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return await FailAsync(job, databasePath, FailureCategories.SourceMissing, $"Original does not exist: {path}", ExitCode.ProcessingFailure, progress, cancellationToken);
            }
            job.SourceFingerprint = fingerprint;
            var verdict = await CheckSourceAsync(path, job.SourceKind, settings, force, cancellationToken);
            // Saved with the job's next update. This covers a job queued before migration 8; for the rest it describes the
            // file as this run found it.
            if (verdict.Media is not null) job.VideoFacts = verdict.Media.ToVideoFacts(verdict.Info.Length);
            if (verdict.Skip) return await SkipAsync(job, databasePath, settings, verdict.Message!, progress, cancellationToken);
            if (!verdict.Proceeds) return await FailAsync(job, databasePath, verdict.FailureCategory!, verdict.Message!, verdict.ExitCode, progress, cancellationToken);
            var (info, media, cap) = (verdict.Info, verdict.Media!, verdict.Cap!);

            var sampleCount = CrfSampleCountCalculator.Calculate(media.DurationSeconds, settings.Quality.CrfSearch.SampleCount);
            var quality = settings.Quality.WithSampleCount(sampleCount).WithSizeCap(cap);
            // Both questions are settled before anything is staged: staging hashes the original and needs a worker, and a
            // resume that only has to download or check what is already finished needs neither (VOPT-72, VOPT-89).
            var canReuseOutput = CanReuseOutput(job, resumeStatus);
            if (session.IsRemote && !canReuseOutput) await SweepFinishedWorkspacesOnceAsync(session, databasePath, cancellationToken);
            var reattachAttempt = session.IsRemote && resumeStatus is JobStatus.Encoding or JobStatus.Downloading && job.Attempt > 0;
            // The finished encode is looked for on the worker, not assumed from the status, so the job is recorded as
            // Downloading only once that is known: a stop in between would otherwise say an encode is still running that is not
            // (VOPT-86). A worker that lost it, such as a deleted managed server, answers no and the stages run again.
            var finishedOnWorker = !canReuseOutput && reattachAttempt && job.Crf is not null
                && await session.HasFinishedEncodeAsync(EncodingOutputPath(path, job), job.Attempt, cancellationToken);
            if (session.IsRemote && !canReuseOutput && !finishedOnWorker)
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
                var samples = HumanReadableValues.TryParseDuration(quality.CrfSearch.SampleDuration, out var sampleDuration) && CrfSampleCountCalculator.TestsWholeVideo(media.DurationSeconds, quality.CrfSearch.SampleCount, sampleDuration)
                    ? "testing the whole video"
                    : $"{quality.CrfSearch.SampleCount.ToString(CultureInfo.InvariantCulture)} test {(quality.CrfSearch.SampleCount == 1 ? "clip" : "clips")} of {quality.CrfSearch.SampleDuration} each";
                var saving = cap.SetByMinimumBytes ? $" that saves {cap.MinimumText}" : string.Empty;
                progress?.Report(JobProgress.Step($"Finding the best CRF for VMAF {quality.MinimumVmaf.ToString("0.##", CultureInfo.InvariantCulture)}{saving} ({samples})..."));
                var crfResult = await session.SearchCrfAsync(path, quality, Translating(progress, AbAv1Stage.CrfSearch, quality), cancellationToken);
                job.Crf = crfResult.Crf;
                // The settings that chose this CRF, so calibrate counts the eventual conversion only under them.
                job.QualityFingerprint = QualityFingerprint.Compute(settings);
                prediction = crfResult.Prediction;
                await jobs.UpdateAsync(databasePath, job, cancellationToken);
                // A search this command joined midway has no start time to measure from.
                var took = crfResult.Duration is { } duration ? $" in {HumanReadableValues.FormatDuration(duration)}" : string.Empty;
                progress?.Report(JobProgress.Step(prediction is null
                    ? $"Chose CRF {crfResult.Crf.ToString(CultureInfo.InvariantCulture)}{took}."
                    : $"Chose CRF {crfResult.Crf.ToString(CultureInfo.InvariantCulture)}{took}. Expected output: {HumanReadableValues.FormatSizeText(prediction.Size)} ({prediction.Percent.ToString(CultureInfo.InvariantCulture)}% of the original)."));
            }

            OutputManifest manifest;
            if (canReuseOutput)
            {
                manifest = await manifests.LoadAsync(job.OutputPath!, cancellationToken);
            }
            else
            {
                stage = "EncodeFailed";
                if (!reattachAttempt) job.Attempt++;
                var outputPath = EncodingOutputPath(path, job);
                job.OutputPath = outputPath;
                job.ManifestPath = manifests.GetPath(outputPath);
                var encodedPath = outputPath;
                if (finishedOnWorker)
                {
                    stage = "DownloadFailed";
                    job.Status = JobStatus.Downloading;
                    await jobs.UpdateAsync(databasePath, job, cancellationToken);
                    // A rerun after a stopped download watched the encode end; one after a stopped encode did not.
                    if (resumeStatus == JobStatus.Encoding) progress?.Report(JobProgress.Step("The encode on the worker had already finished."));
                }
                else
                {
                    job.Status = JobStatus.Encoding;
                    await jobs.UpdateAsync(databasePath, job, cancellationToken);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    var crf = job.Crf!.Value.ToString(CultureInfo.InvariantCulture);
                    progress?.Report(JobProgress.Step(prediction is null ? $"Encoding at CRF {crf}..." : $"Encoding at CRF {crf}, expected to take about {prediction.Taking}..."));
                    var encodeResult = await session.EncodeAsync(path, outputPath, job.Crf.Value, quality, job.Attempt, PcmAudioStreams(media), Translating(progress, AbAv1Stage.Encode, quality), cancellationToken);
                    encodedPath = encodeResult.OutputPath;
                    if (session.IsRemote)
                    {
                        stage = "DownloadFailed";
                        job.Status = JobStatus.Downloading;
                        await jobs.UpdateAsync(databasePath, job, cancellationToken);
                    }
                }
                if (session.IsRemote) await session.RetrieveOutputAsync(outputPath, job.Attempt, progress, cancellationToken);
                manifest = new OutputManifest { SourcePath = path, SourceFingerprint = fingerprint, OutputPath = encodedPath, Crf = job.Crf!.Value, CreatedUtc = DateTimeOffset.UtcNow };
                await manifests.SaveAsync(manifest, cancellationToken);
                job.OutputPath = encodedPath;
                job.ManifestPath = manifests.GetPath(encodedPath);
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
            // A validation resumed on this PC needs no worker, and this run may have none to reach. The workspace it leaves is
            // removed by the sweep of the workspaces of finished jobs, which a run makes on the worker once this job has ended
            // (VOPT-90).
            if (session.IsRemote && !canReuseOutput) await session.CleanupAsync(cancellationToken);
            if (!manifest.Validation.Passed)
            {
                return await FailAsync(job, databasePath, "ValidationFailed", string.Join("; ", manifest.Validation.Failures), ExitCode.ValidationFailure, progress, cancellationToken);
            }

            job.Status = JobStatus.ReadyToFinalize;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            // Console output stays ASCII: redirected output and legacy consoles use a code page that drops characters like "→".
            progress?.Report(JobProgress.Outcome(JobTone.Success, $"Ready to finalise: {HumanReadableValues.FormatSize(job.SourceSizeBytes ?? 0)} -> {HumanReadableValues.FormatSize(job.OutputSizeBytes ?? 0)}, {(job.PercentageSaved ?? 0).ToString("F1", CultureInfo.InvariantCulture)}% saved."));
            return new JobProcessingResult(job, ExitCode.Success, $"Job {job.Id:N} is ready to finalise.");
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C stops only this side. A remote stage runs detached and keeps going: the worker bills hourly whether or not
            // it is busy, so stopping the stage would only lose the work, and the rerun reattaches to it instead.
            var stoppedIn = ResumePoint(job.Status, resumeStatus);
            job.ResumeStatus = stoppedIn;
            job.Status = JobStatus.Interrupted;
            job.FailureCategory = "Interrupted";
            job.FailureMessage = "Stopped before it finished.";
            await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
            throw new JobInterruptedException(job, stoppedIn, session.IsRemote, cancellationToken);
        }
        catch (NoWorthwhileCrfException exception)
        {
            return await SkipAsync(job, databasePath, settings, exception.Message, progress, cancellationToken);
        }
        catch (RemoteConnectionException exception)
        {
            job.ResumeStatus = ResumePoint(job.Status, resumeStatus);
            job.Status = JobStatus.Interrupted;
            job.FailureCategory = FailureCategories.RemoteUnavailable;
            job.FailureMessage = exception.Message;
            await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
            // How to resume comes once, from the command, after the run stops: it depends on the worker kind and the command.
            progress?.Report(JobProgress.Outcome(JobTone.Failure, $"Worker unreachable: {exception.Message.TrimEnd('.')}."));
            return new JobProcessingResult(job, ExitCode.ProcessingFailure, exception.Message);
        }
        // A job held or dropped since the run read it fails this write too, so its JobChangedException reaches the run,
        // which passes over the job.
        catch (Exception exception)
        {
            return await FailAsync(job, databasePath, stage, exception.Message, ExitCode.ProcessingFailure, progress, cancellationToken, (exception as AbAv1StageException)?.LogPath);
        }
    }

    /// <summary>
    /// Whether <see cref="ProcessAsync"/> would reach a remote stage for this original, so a run creates a server only when a
    /// job will use it (VOPT-68). It makes the same decisions in the same order and writes nothing. A condition that can
    /// clear before the job runs (a file not ready yet, a failed probe) counts as needing it.
    /// </summary>
    public async Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(settings.Processing.Mode, ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase)) return false;
        var path = Path.GetFullPath(job.SourcePath);
        if (!File.Exists(path)) return false;
        if (!job.IsQueued) return false;
        // The AV1 is on this PC, so its validation runs here whatever the job did on the worker.
        if (CanReuseOutput(job, job.ResumeStatus)) return false;
        if (JobCompatibility.Evaluate(job, settings).Status == JobCompatibilityStatus.Incompatible) return false;
        // A file still being written or briefly unreadable by ffprobe may be fine by the time the job runs, and a job that
        // reaches a remote stage with no worker ensured stops with nothing to resume on. So a passing condition that can
        // change counts as needing the worker; only the lasting refusals (roots, rules, size) count as not.
        try
        {
            var verdict = await CheckSourceAsync(path, job.SourceKind, settings, job.Forced, cancellationToken);
            return verdict.Proceeds || verdict.FailureCategory == FailureCategories.SourceNotReady;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return true;
        }
    }

    private readonly HashSet<string> _sweptWorkers = [];

    /// <summary>
    /// The first time a run reaches a worker, ends what dropped videos left running there and frees their disk (VOPT-90).
    /// Only the workspaces of jobs this database holds as ended are touched: a stopped job is resumed by a later run, and a job
    /// this database has never heard of may belong to another database sharing a manual worker. It is housekeeping, so a worker
    /// that cannot answer does not fail the video; the video's own stage meets the same error and reports it properly. A worker
    /// that failed is not asked again in this run.
    /// </summary>
    private async Task SweepFinishedWorkspacesOnceAsync(IProcessingSession session, string databasePath, CancellationToken cancellationToken)
    {
        lock (_sweptWorkers)
        {
            if (!_sweptWorkers.Add(session.RemoteHost ?? string.Empty)) return;
        }
        try
        {
            var finished = (await jobs.ListAsync(databasePath, terminal: true, cancellationToken)).Select(job => job.Id).ToHashSet();
            await session.RemoveFinishedWorkspacesAsync(finished, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    /// <summary>A job stopped while its AV1 was being checked, with that AV1 and its manifest still on this PC: nothing is left to encode or fetch.</summary>
    private bool CanReuseOutput(JobRecord job, JobStatus? resumeStatus) =>
        resumeStatus == JobStatus.Validating && job.OutputPath is not null && File.Exists(job.OutputPath) && File.Exists(manifests.GetPath(job.OutputPath));

    private static string EncodingOutputPath(string sourcePath, JobRecord job) =>
        Path.Combine(Path.GetDirectoryName(sourcePath)!, TemporaryOutputClassifier.FolderName, $"{Path.GetFileNameWithoutExtension(sourcePath)}.{job.Id:N}.{job.Attempt}.encoding{OutputContainer.ExtensionFor(sourcePath)}");

    /// <summary>
    /// The checks on the original before anything is staged, shared by <see cref="ProcessAsync"/> and <see cref="NeedsWorkerAsync"/>
    /// so the prediction cannot drift from processing. Reads only; a probe failure throws.
    /// </summary>
    private async Task<SourceVerdict> CheckSourceAsync(string path, JobSourceKind kind, AppSettings settings, bool force, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        // An Immich job's original is the staged copy, which lives under immich.stagingDirectory rather than a watch root.
        if (!force && kind == JobSourceKind.Immich && !(settings.Immich.StagingDirectory.Length > 0 && IsWithinRoot(path, settings.Immich.StagingDirectory))) return SourceVerdict.Fail(info, "SourceOutsideWatchRoot", "Staged Immich original is outside immich.stagingDirectory. Use --force to bypass this check.", ExitCode.ProcessingFailure);
        if (!force && kind == JobSourceKind.File && !settings.Watch.Roots.Any(root => IsWithinRoot(path, root.Path))) return SourceVerdict.Fail(info, "SourceOutsideWatchRoot", "Original is outside configured watch roots. Use --force to bypass this check.", ExitCode.ProcessingFailure);
        var readinessResult = await readiness.CheckAsync(path, cancellationToken);
        if (!readinessResult.IsReady) return SourceVerdict.Fail(info, FailureCategories.SourceNotReady, $"Original is not ready: {readinessResult.Reason}", ExitCode.ProcessingFailure);

        var media = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(path, cancellationToken);
        if (!force && !EligibilityEvaluator.IsEligible(info, media, settings.Eligibility, out var eligibilityReason)) return SourceVerdict.Fail(info, "Ineligible", eligibilityReason, ExitCode.NoEligibleFiles) with { Media = media };

        // Before staging, and whatever --force says: a video that cannot save savings.minimumBytesSaved would only fail
        // validation after a full upload, search and encode.
        var cap = SizeCap.For(media, info.Length, settings.Quality, settings.Savings);
        if (cap.RulesOutEverything)
        {
            var measured = cap.MeasuredIsFile ? $"the file is {HumanReadableValues.FormatSize(cap.MeasuredBytes)}" : $"the video stream is about {HumanReadableValues.FormatSize(cap.MeasuredBytes)}";
            return new SourceVerdict(info) { Media = media, Skip = true, Message = $"{measured}, so it cannot save {cap.MinimumText} (savings.minimumBytesSaved)." };
        }
        return new SourceVerdict(info) { Media = media, Cap = cap };
    }

    /// <summary>What <see cref="CheckSourceAsync"/> found. <see cref="Media"/> is set once the probe ran, so a refused job still records its video facts.</summary>
    private sealed record SourceVerdict(FileInfo Info)
    {
        public MediaInfo? Media { get; init; }
        public SizeCap? Cap { get; init; }
        public string? FailureCategory { get; init; }
        public string? Message { get; init; }
        public ExitCode ExitCode { get; init; }
        public bool Skip { get; init; }
        public bool Proceeds => FailureCategory is null && !Skip;

        public static SourceVerdict Fail(FileInfo info, string category, string message, ExitCode exitCode) =>
            new(info) { FailureCategory = category, Message = message, ExitCode = exitCode };
    }

    /// <summary>
    /// The stage a rerun resumes from. A job stopped before it reached a stage of its own in this run keeps its earlier
    /// resume point: a remote encode an earlier command left running must be reattached to, and a new attempt would start
    /// a second encode beside it.
    /// </summary>
    internal static JobStatus ResumePoint(JobStatus current, JobStatus? earlier) =>
        current is JobStatus.Queued or JobStatus.Staging && earlier is { } resume ? resume : current;

    /// <summary>The audio-relative indexes of the PCM streams. Browsers do not play PCM copied into MP4, so these are re-encoded to FLAC.</summary>
    private static int[] PcmAudioStreams(MediaInfo media) =>
        [.. (media.AudioCodecs ?? []).Select((codec, index) => (codec, index)).Where(stream => stream.codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)).Select(stream => stream.index)];

    private IProgress<JobProgress>? Translating(IProgress<JobProgress>? progress, AbAv1Stage stage, QualitySettings quality) =>
        progress is null ? null : translator.Wrap(progress, stage, quality);

    /// <summary>
    /// Matches by job ID and attempt rather than by <see cref="JobRecord.OutputPath"/>, because ab-av1 writes the encode
    /// under a temporary name of its own and renames it only on success. A failed delete is reported, not fatal: the new
    /// attempt writes a different file, and <c>cleanup</c> can remove this one later, for example once an ab-av1 orphaned
    /// by a crash releases it.
    /// </summary>
    private void DeletePartialOutput(JobRecord job, IProgress<JobProgress>? progress)
    {
        var folder = Path.Combine(Path.GetDirectoryName(job.SourcePath)!, TemporaryOutputClassifier.FolderName);
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (!TemporaryOutputClassifier.TryParse(Path.GetFileName(file), out var jobId, out var attempt) || jobId != job.Id || attempt != job.Attempt) continue;
            try
            {
                File.Delete(file);
                progress?.Report(JobProgress.Step("Deleted the unfinished AV1 from the stopped encode."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                progress?.Report(JobProgress.Step($"Could not delete the unfinished AV1 from the stopped encode ({exception.Message}); run '{hints.Command("cleanup")}' later to remove {Path.GetFileName(file)}."));
            }
        }
    }

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
        progress?.Report(JobProgress.Outcome(JobTone.Failure, category switch
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
    /// Ends the job as skipped: AV1 cannot meet the quality bar and the size cap together. The fingerprint lets discovery
    /// check the video again once those settings change. A remote search has already cleaned its workspace on the way out;
    /// a skip before the search staged nothing in this run.
    /// </summary>
    private async Task<JobProcessingResult> SkipAsync(JobRecord job, string databasePath, AppSettings settings, string reason, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Skipped;
        job.FailureCategory = FailureCategories.NotWorthConverting;
        job.FailureMessage = reason;
        job.QualityFingerprint = QualityFingerprint.Compute(settings);
        job.CompletedUtc = DateTimeOffset.UtcNow;
        await jobs.UpdateAsync(databasePath, job, cancellationToken);
        progress?.Report(JobProgress.Outcome(JobTone.Warning, $"Not worth converting: {reason}"));
        return new JobProcessingResult(job, ExitCode.Success, reason);
    }

    private static bool IsWithinRoot(string sourcePath, string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
        return sourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
