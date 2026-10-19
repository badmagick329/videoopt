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
        // again once it is back. A job with an encode on the worker stays stopped instead, before anything below changes it.
        if (!File.Exists(path)) return await OriginalMissingAsync(job, databasePath, job.ResumeStatus, path, progress, cancellationToken);

        var resumeStatus = job.ResumeStatus;
        if (await CheckCompatibilityAsync(job, databasePath, settings, progress, cancellationToken) is { } incompatible) return incompatible;
        if (job.Status == JobStatus.Interrupted) await RequeueStoppedAsync(job, databasePath, resumeStatus, progress, cancellationToken);

        await using var session = sessions.Create(job, settings);
        var stages = new StageContext(databasePath, job, session, path, progress);
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
                // The same choice as the exists check above, made on the resume point read before the job was requeued.
                return await OriginalMissingAsync(job, databasePath, resumeStatus, path, progress, cancellationToken);
            }
            // Settled before the plan decides what to reuse; an empty stored fingerprint has nothing to compare.
            var changed = ChangedSinceLastRun(job, fingerprint);
            if (changed) resumeStatus = await DiscardEarlierWorkAsync(job, resumeStatus, session, progress, cancellationToken);
            job.SourceFingerprint = fingerprint;
            var verdict = await CheckSourceAsync(path, job.SourceKind, settings, job.Forced, cancellationToken);
            // Saved with the job's next update. This covers a job queued before migration 8; for the rest it describes the
            // file as this run found it.
            if (verdict.Media is not null) job.VideoFacts = verdict.Media.ToVideoFacts(verdict.Info.Length);
            if (verdict.Skip) return await SkipAsync(job, databasePath, settings, verdict.Message!, progress, cancellationToken, verdict.SkipCategory);
            if (!verdict.Proceeds) return await FailAsync(job, databasePath, verdict.FailureCategory!, verdict.Message!, verdict.ExitCode, progress, cancellationToken);
            var (media, cap) = (verdict.Media!, verdict.Cap!);
            // Saved with the job's next update, so the note is there from the first stage on and a new attempt replaces it.
            job.Notes = AudioPlan.For(media) is { Dropped.Count: > 0 } audio ? [DroppedAudioNote(audio)] : [];
            var sampleCount = CrfSampleCountCalculator.Calculate(media.DurationSeconds, settings.Quality.CrfSearch.ConfiguredSampleCount);
            var quality = settings.Quality.WithSampleCount(sampleCount).WithSizeCap(cap);

            // Planned before anything is staged. The worker is asked about a finished encode only after the sweep, and the
            // finished encode is looked for there, not assumed from the status, so the job is recorded as Downloading only once
            // that is known: a stop in between would otherwise say an encode is still running that is not (VOPT-86). A worker
            // that lost it, such as a deleted managed server, answers no and the stages run again.
            var outputOnDisk = OutputOnDisk(job);
            ResumePlan PlanFor(bool finishedOnWorker) => ResumePlan.Plan(job, resumeStatus, changed, session.IsRemote, outputOnDisk, finishedOnWorker);
            var plan = PlanFor(finishedOnWorker: false);
            if (plan.UsesWorker) await SweepFinishedWorkspacesOnceAsync(session, databasePath, cancellationToken);
            if (plan.AsksWorkerForFinishedEncode && await session.HasFinishedEncodeAsync(EncodingOutputPath(path, job), job.Attempt, cancellationToken)) plan = PlanFor(finishedOnWorker: true);

            if (plan.Stages && await StageAsync(stages, fingerprint, cancellationToken) is { } changedDuringUpload) return changedDuringUpload;
            var prediction = plan.SearchesCrf ? await SearchCrfAsync(stages, settings, media, cap, quality, cancellationToken) : null;
            OutputManifest manifest;
            if (plan.ReusesOutput)
            {
                manifest = await manifests.LoadAsync(job.OutputPath!, cancellationToken);
            }
            else
            {
                var outputPath = BeginAttempt(job, path, plan);
                var encodedPath = plan.Encodes ? await EncodeAsync(stages, outputPath, media, quality, prediction, cancellationToken) : outputPath;
                if (plan.Downloads) await DownloadAsync(stages, outputPath, plan, cancellationToken);
                manifest = await RecordOutputAsync(job, path, fingerprint, encodedPath, cancellationToken);
            }
            return await ValidateAsync(stages, settings, manifest, plan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C stops only this side. A remote stage runs detached and keeps going: the worker bills hourly whether or not
            // it is busy, so stopping the stage would only lose the work, and the rerun reattaches to it instead.
            var stoppedIn = ResumePoint(job.Status, resumeStatus);
            job.ResumeStatus = stoppedIn;
            job.Status = JobStatus.Interrupted;
            job.FailureCategory = FailureCategories.Interrupted;
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
        // Its own category rather than the stage's: the tool hung rather than failed, and the message says which stage.
        catch (RemoteStageStalledException exception)
        {
            return await FailAsync(job, databasePath, FailureCategories.StageStalled, exception.Message, ExitCode.ProcessingFailure, progress, cancellationToken, exception.LogPath);
        }
        // A job held or dropped since the run read it fails this write too, so its JobChangedException reaches the run,
        // which passes over the job.
        catch (Exception exception)
        {
            return await FailAsync(job, databasePath, stages.FailureCategory, exception.Message, ExitCode.ProcessingFailure, progress, cancellationToken, (exception as AbAv1StageException)?.LogPath);
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
        // The AV1 is on this PC, so its validation runs here whatever the job did on the worker; unless the original changed
        // since, when the job starts again on the worker. The plan is ProcessAsync's, so the two cannot disagree; the original
        // is hashed only when the answer can change it.
        var outputOnDisk = OutputOnDisk(job);
        var plan = ResumePlan.Plan(job, job.ResumeStatus, originalChanged: false, sessionIsRemote: true, outputOnDisk);
        if (!plan.UsesWorker && await ChangedSinceLastRunAsync(job, path, cancellationToken)) plan = ResumePlan.Plan(job, job.ResumeStatus, originalChanged: true, sessionIsRemote: true, outputOnDisk);
        if (!plan.UsesWorker) return false;
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
            await session.StopAndRemoveWorkspacesAsync(finished, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    /// <summary>What <see cref="ProcessAsync"/>'s stages share. <see cref="FailureCategory"/> is what an unexpected error fails the
    /// job with, so each stage sets its own as it starts; before the first stage it is the plain processing failure.</summary>
    private sealed class StageContext(string databasePath, JobRecord job, IProcessingSession session, string path, IProgress<JobProgress>? progress)
    {
        public string DatabasePath => databasePath;
        public JobRecord Job => job;
        public IProcessingSession Session => session;
        public string Path => path;
        public IProgress<JobProgress>? Progress => progress;
        public string FailureCategory { get; set; } = FailureCategories.ProcessingFailed;
    }

    /// <summary>The original isn't there: the job fails as <c>SourceMissing</c>, unless the worker may hold its encode, when it stays stopped (<see cref="KeepStoppedAsync"/>).</summary>
    private Task<JobProcessingResult> OriginalMissingAsync(JobRecord job, string databasePath, JobStatus? resumeStatus, string path, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        ResumePlan.MayHaveEncodeOnWorker(job, resumeStatus)
            ? KeepStoppedAsync(job, databasePath, resumeStatus!.Value, path, progress, cancellationToken)
            : FailAsync(job, databasePath, FailureCategories.SourceMissing, $"Original does not exist: {path}", ExitCode.ProcessingFailure, progress, cancellationToken);

    /// <summary>Fails a started job the settings can no longer resume, and binds a job never started to the current ones.</summary>
    /// <returns>The failure, or null when the job goes on.</returns>
    private async Task<JobProcessingResult?> CheckCompatibilityAsync(JobRecord job, string databasePath, AppSettings settings, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var compatibility = JobCompatibility.Evaluate(job, settings);
        if (compatibility.Status == JobCompatibilityStatus.Incompatible)
        {
            return await FailAsync(job, databasePath, FailureCategories.RemoteConfigurationChanged, compatibility.Message!, ExitCode.InvalidConfiguration, progress, cancellationToken);
        }
        if (compatibility.Status == JobCompatibilityStatus.Adopted)
        {
            JobCompatibility.Apply(job, compatibility.ExpectedBinding);
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
        }
        return null;
    }

    /// <summary>
    /// Queues a stopped job again, dropping what its stop leaves unusable: a local encode cannot continue its partial file, and a
    /// local search stopped midway is run again. <paramref name="resumeStatus"/> is where it stopped, kept by the caller for the plan.
    /// </summary>
    private async Task RequeueStoppedAsync(JobRecord job, string databasePath, JobStatus? resumeStatus, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (TemporaryOutputClassifier.RestartsEncode(job))
        {
            DeletePartialOutput(job, progress);
            job.OutputPath = null;
            job.ManifestPath = null;
        }
        else if (resumeStatus == JobStatus.CrfSearching && !string.Equals(job.ExecutionMode, ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase))
        {
            job.Crf = null;
        }
        job.Status = JobStatus.Queued;
        job.ResumeStatus = null;
        job.FailureCategory = null;
        job.FailureMessage = null;
        await jobs.UpdateAsync(databasePath, job, cancellationToken);
    }

    /// <summary>Whether the job's AV1 and its manifest are both on this PC, for <see cref="ResumePlan.Plan"/>.</summary>
    private bool OutputOnDisk(JobRecord job) =>
        job.OutputPath is not null && File.Exists(job.OutputPath) && File.Exists(manifests.GetPath(job.OutputPath));

    /// <summary>Uploads the original, then checks it did not change while it went.</summary>
    /// <returns>The job failed as <c>SourceChanged</c> when it did, otherwise null.</returns>
    private async Task<JobProcessingResult?> StageAsync(StageContext stages, string fingerprint, CancellationToken cancellationToken)
    {
        stages.FailureCategory = FailureCategories.StagingFailed;
        var job = stages.Job;
        job.Status = JobStatus.Staging;
        await jobs.UpdateAsync(stages.DatabasePath, job, cancellationToken);
        await stages.Session.StageAsync(stages.Path, fingerprint, stages.Progress, cancellationToken);
        if (string.Equals(await fingerprints.CreateAsync(stages.Path, cancellationToken), fingerprint, StringComparison.Ordinal)) return null;
        await WorkspaceCleanup.TryAsync(stages.Session.CleanupAsync, stages.Progress, cancellationToken);
        return await FailAsync(job, stages.DatabasePath, FailureCategories.SourceChanged, "Original changed while it was being uploaded.", ExitCode.ProcessingFailure, stages.Progress, cancellationToken);
    }

    /// <returns>ab-av1's estimate of the encode, when it printed one.</returns>
    private async Task<CrfPrediction?> SearchCrfAsync(StageContext stages, AppSettings settings, MediaInfo media, SizeCap cap, QualitySettings quality, CancellationToken cancellationToken)
    {
        stages.FailureCategory = FailureCategories.CrfSearchFailed;
        var (job, progress) = (stages.Job, stages.Progress);
        job.Status = JobStatus.CrfSearching;
        await jobs.UpdateAsync(stages.DatabasePath, job, cancellationToken);
        var samples = HumanReadableValues.TryParseDuration(quality.CrfSearch.SampleDuration, out var sampleDuration) && CrfSampleCountCalculator.TestsWholeVideo(media.DurationSeconds, quality.CrfSearch.SampleCount, sampleDuration)
            ? "testing the whole video"
            : $"{quality.CrfSearch.SampleCount.ToString(CultureInfo.InvariantCulture)} test {(quality.CrfSearch.SampleCount == 1 ? "clip" : "clips")} of {quality.CrfSearch.SampleDuration} each";
        var saving = cap.SetByMinimumBytes ? $" that saves {cap.MinimumText}" : string.Empty;
        progress?.Report(JobProgress.Step($"Finding the best CRF for VMAF {quality.MinimumVmaf.ToString("0.##", CultureInfo.InvariantCulture)}{saving} ({samples})."));
        var crfResult = await stages.Session.SearchCrfAsync(stages.Path, quality, Translating(progress, AbAv1Stage.CrfSearch, quality), cancellationToken);
        job.Crf = crfResult.Crf;
        // The settings that chose this CRF, so calibrate counts the eventual conversion only under them.
        job.QualityFingerprint = QualityFingerprint.Compute(settings);
        var prediction = crfResult.Prediction;
        await jobs.UpdateAsync(stages.DatabasePath, job, cancellationToken);
        // A search this command joined midway has no start time to measure from.
        var took = crfResult.Duration is { } duration ? $" in {HumanReadableValues.FormatDuration(duration)}" : string.Empty;
        progress?.Report(JobProgress.Step(prediction is null
            ? $"Chose CRF {crfResult.Crf.ToString(CultureInfo.InvariantCulture)}{took}."
            : $"Chose CRF {crfResult.Crf.ToString(CultureInfo.InvariantCulture)}{took}. Expected output: {HumanReadableValues.FormatSizeText(prediction.Size)} ({prediction.Percent.ToString(CultureInfo.InvariantCulture)}% of the original)."));
        return prediction;
    }

    /// <summary>Counts a new attempt when the plan makes one and points the job at that attempt's AV1 and manifest.</summary>
    /// <returns>Where the attempt's AV1 is written, or fetched to.</returns>
    private string BeginAttempt(JobRecord job, string path, ResumePlan plan)
    {
        if (plan.NewAttempt) job.Attempt++;
        var outputPath = EncodingOutputPath(path, job);
        job.OutputPath = outputPath;
        job.ManifestPath = manifests.GetPath(outputPath);
        return outputPath;
    }

    /// <returns>The AV1 the encoder wrote, which may differ from <paramref name="outputPath"/>.</returns>
    private async Task<string> EncodeAsync(StageContext stages, string outputPath, MediaInfo media, QualitySettings quality, CrfPrediction? prediction, CancellationToken cancellationToken)
    {
        stages.FailureCategory = FailureCategories.EncodeFailed;
        var job = stages.Job;
        job.Status = JobStatus.Encoding;
        await jobs.UpdateAsync(stages.DatabasePath, job, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var crf = job.Crf!.Value.ToString(CultureInfo.InvariantCulture);
        stages.Progress?.Report(JobProgress.Step(prediction is null ? $"Encoding at CRF {crf}." : $"Encoding at CRF {crf}, expected to take about {prediction.Taking}."));
        var encodeResult = await stages.Session.EncodeAsync(stages.Path, outputPath, job.Crf.Value, quality, job.Attempt, AudioPlan.For(media), Translating(stages.Progress, AbAv1Stage.Encode, quality), cancellationToken);
        return encodeResult.OutputPath;
    }

    /// <summary>Fetches the attempt's AV1 from the worker, recorded as Downloading before anything can stop it.</summary>
    private async Task DownloadAsync(StageContext stages, string outputPath, ResumePlan plan, CancellationToken cancellationToken)
    {
        stages.FailureCategory = FailureCategories.DownloadFailed;
        var job = stages.Job;
        job.Status = JobStatus.Downloading;
        await jobs.UpdateAsync(stages.DatabasePath, job, cancellationToken);
        if (plan.SaysEncodeFinished) stages.Progress?.Report(JobProgress.Step("The encode on the worker had already finished."));
        await stages.Session.RetrieveOutputAsync(outputPath, job.Attempt, stages.Progress, cancellationToken);
    }

    /// <summary>Writes the manifest of the AV1 this run made or fetched, and points the job at both.</summary>
    private async Task<OutputManifest> RecordOutputAsync(JobRecord job, string path, string fingerprint, string encodedPath, CancellationToken cancellationToken)
    {
        var manifest = new OutputManifest { SourcePath = path, SourceFingerprint = fingerprint, OutputPath = encodedPath, Crf = job.Crf!.Value, CreatedUtc = DateTimeOffset.UtcNow };
        await manifests.SaveAsync(manifest, cancellationToken);
        job.OutputPath = encodedPath;
        job.ManifestPath = manifests.GetPath(encodedPath);
        return manifest;
    }

    /// <summary>Checks the AV1, writes the verdict, then clears the job's workspace on the worker.</summary>
    private async Task<JobProcessingResult> ValidateAsync(StageContext stages, AppSettings settings, OutputManifest manifest, ResumePlan plan, CancellationToken cancellationToken)
    {
        stages.FailureCategory = FailureCategories.ValidationFailed;
        var (job, progress) = (stages.Job, stages.Progress);
        job.Status = JobStatus.Validating;
        job.ResumeStatus = null;
        await jobs.UpdateAsync(stages.DatabasePath, job, cancellationToken);
        progress?.Report(JobValidation.CheckingStep(settings));
        manifest.Validation = await validator.ValidateAsync(manifest, settings, cancellationToken);
        var outcome = await JobValidation.RecordAsync(job, manifest, settings, manifests);
        await jobs.UpdateAsync(stages.DatabasePath, job, CancellationToken.None);
        // After the verdict is written, so a cleanup that fails cannot change it (VOPT-157). A validation resumed on this PC
        // needs no worker, and this run may have none to reach. The workspace it leaves is removed by the sweep of the
        // workspaces of finished jobs, which a run makes on the worker once this job has ended (VOPT-90).
        if (plan.UsesWorker) await WorkspaceCleanup.TryAsync(stages.Session.CleanupAsync, progress, cancellationToken);
        foreach (var line in JobValidation.Describe(job, outcome)) progress?.Report(line);
        return outcome switch
        {
            ValidationVerdict.Failed => new JobProcessingResult(job, ExitCode.ValidationFailure, job.FailureMessage!),
            ValidationVerdict.NotWorthConverting => new JobProcessingResult(job, ExitCode.Success, job.FailureMessage!),
            _ => new JobProcessingResult(job, ExitCode.Success, $"Job {job.Id:N} is ready to finalise.")
        };
    }

    /// <summary>
    /// Leaves a job whose original can't be reached stopped where it was, rather than failing it (VOPT-160). A failed job is
    /// ended, so the next run's sweep would delete its workspace on the worker and the encode with it; an original on a NAS or
    /// USB drive that is offline comes back. It counts as neither failed nor done, so the run goes on to the next video. The
    /// category replaces whatever an earlier stop left, such as RemoteUnavailable, which would make the run stop as if the
    /// worker had gone; the requeue of the next run clears it.
    /// </summary>
    private async Task<JobProcessingResult> KeepStoppedAsync(JobRecord job, string databasePath, JobStatus resumeStatus, string path, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Interrupted;
        job.ResumeStatus = resumeStatus;
        job.FailureCategory = FailureCategories.SourceMissing;
        job.FailureMessage = $"Original isn't reachable: {path}";
        await jobs.UpdateAsync(databasePath, job, cancellationToken);
        progress?.Report(JobProgress.Outcome(JobTone.Warning, "Original isn't reachable. The video stays stopped and resumes once it's back."));
        return new JobProcessingResult(job, ExitCode.Success, job.FailureMessage);
    }

    /// <summary>Whether the original differs from the one the job last read; a job with no stored fingerprint has nothing to compare.</summary>
    private static bool ChangedSinceLastRun(JobRecord job, string fingerprint) =>
        job.SourceFingerprint.Length > 0 && !string.Equals(job.SourceFingerprint, fingerprint, StringComparison.Ordinal);

    /// <summary>
    /// <see cref="ChangedSinceLastRun(JobRecord, string)"/> for the prediction. An original that cannot be read counts as
    /// unchanged: processing fails it before any remote stage.
    /// </summary>
    private async Task<bool> ChangedSinceLastRunAsync(JobRecord job, string path, CancellationToken cancellationToken)
    {
        try
        {
            return ChangedSinceLastRun(job, await fingerprints.CreateAsync(path, cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// The original changed since the job last ran, as when someone fixes its date or place in another app, so everything the job
    /// made is of the old content: its CRF, the AV1 on this PC, and the upload, search and encode on the worker (VOPT-138).
    /// Reattaching or reusing any of it would finalise an AV1 of the old video under the new fingerprint, and lose the edits.
    /// The worker's workspace is stopped and removed before the job changes, because removing it does not end a running
    /// ab-av1 and a new search there would pick up the old one's result; a worker that cannot be reached leaves the job as it
    /// was, so the next run finds the change again. A job with nothing made yet, such as one whose file changed after
    /// <c>queue add</c>, just goes on.
    /// </summary>
    /// <returns>The resume point the job now has: none, so it stages again and makes a new attempt.</returns>
    private async Task<JobStatus?> DiscardEarlierWorkAsync(JobRecord job, JobStatus? resumeStatus, IProcessingSession session, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (job.Crf is null && resumeStatus is null) return null;
        progress?.Report(JobProgress.Step("The original changed after this video was stopped, so the conversion starts over."));
        if (session.IsRemote) await session.StopAndRemoveWorkspacesAsync(new HashSet<Guid> { job.Id }, cancellationToken);
        if (ResumePlan.CanReuseOutput(resumeStatus, OutputOnDisk(job)))
        {
            // The restart line covers a delete that works.
            DeleteAttemptFiles(job, progress, "the AV1 made before the original changed", reportDeleted: false);
            job.OutputPath = null;
            job.ManifestPath = null;
        }
        job.Crf = null;
        job.QualityFingerprint = null;
        return null;
    }

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
        if (!force && kind == JobSourceKind.Immich && !(settings.Immich.StagingDirectory.Length > 0 && IsWithinRoot(path, settings.Immich.StagingDirectory))) return SourceVerdict.Fail(info, FailureCategories.SourceOutsideWatchRoot, "Staged Immich original is outside immich.stagingDirectory. Use --force to bypass this check.", ExitCode.ProcessingFailure);
        if (!force && kind == JobSourceKind.File && !settings.Watch.Roots.Any(root => IsWithinRoot(path, root.Path))) return SourceVerdict.Fail(info, FailureCategories.SourceOutsideWatchRoot, "Original is outside configured watch roots. Use --force to bypass this check.", ExitCode.ProcessingFailure);
        var readinessResult = await readiness.CheckAsync(path, cancellationToken);
        if (!readinessResult.IsReady) return SourceVerdict.Fail(info, FailureCategories.SourceNotReady, $"Original is not ready: {readinessResult.Reason}", ExitCode.ProcessingFailure);

        var media = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(path, cancellationToken);
        // Whatever --force says, which overrides the rules, not this (OutputCodec). It catches a forced job queued before
        // queue add refused AV1s, and a job whose original was replaced by an AV1 after it was queued. Failed as Ineligible,
        // which settles nothing; that is safe because no discovery queues an AV1.
        if (OutputCodec.IsOutputCodec(media.PrimaryVideoCodec)) return SourceVerdict.Fail(info, FailureCategories.Ineligible, OutputCodec.RefusalReason, ExitCode.NoEligibleFiles) with { Media = media };
        if (!force && !EligibilityEvaluator.IsEligible(info, media, settings.Eligibility, out var eligibilityReason)) return SourceVerdict.Fail(info, FailureCategories.Ineligible, eligibilityReason, ExitCode.NoEligibleFiles) with { Media = media };

        // Before staging, and whatever --force says: an AV1 with no audio would pass validation, which expects none, and replace
        // the original at finalise; and a person who chose skip wants the original's spatial audio kept (VOPT-125).
        var audio = AudioPlan.For(media);
        if (audio.LeavesNoAudio) return new SourceVerdict(info) { Media = media, Skip = true, SkipCategory = FailureCategories.UnsupportedAudio, Message = $"FFmpeg can't copy {audio.DroppedTracks}, its only audio, so the AV1 would have no sound." };
        if (audio.Dropped.Count > 0 && settings.Quality.UnsupportedAudio.Equals(UnsupportedAudioActions.Skip, StringComparison.OrdinalIgnoreCase))
        {
            return new SourceVerdict(info) { Media = media, Skip = true, SkipCategory = FailureCategories.UnsupportedAudio, Message = $"FFmpeg can't copy {audio.DroppedTracks}, and quality.unsupportedAudio is skip." };
        }

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
        public string SkipCategory { get; init; } = FailureCategories.NotWorthConverting;
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

    /// <summary>A note, not a validation warning: the person loses only what FFmpeg cannot carry, and a warning would keep every
    /// iPhone 16 video out of batch finalise.</summary>
    private static string DroppedAudioNote(AudioPlan audio)
    {
        var tracks = audio.DroppedTracks;
        return $"{char.ToUpperInvariant(tracks[0])}{tracks[1..]} {(audio.Dropped.Count == 1 ? "was" : "were")} left out because FFmpeg can't copy {(audio.Dropped.Count == 1 ? "it" : "them")}. The other audio is kept.";
    }

    private IProgress<JobProgress>? Translating(IProgress<JobProgress>? progress, AbAv1Stage stage, QualitySettings quality) =>
        progress is null ? null : translator.Wrap(progress, stage, quality);

    private void DeletePartialOutput(JobRecord job, IProgress<JobProgress>? progress) =>
        DeleteAttemptFiles(job, progress, "the unfinished AV1 from the stopped encode", reportDeleted: true);

    /// <summary>
    /// Deletes the files of the job's current attempt: its AV1, partial or finished, and the manifest beside it. Matches by job
    /// ID and attempt rather than by <see cref="JobRecord.OutputPath"/>, because ab-av1 writes the encode under a temporary
    /// name of its own and renames it only on success. A failed delete is reported, not fatal: the new attempt writes a
    /// different file, and <c>cleanup</c> can remove this one later, for example once an ab-av1 orphaned by a crash releases it.
    /// </summary>
    /// <param name="what">The files as the lines name them.</param>
    /// <param name="reportDeleted">Whether each deleted file gets a line; a failed delete always does.</param>
    private void DeleteAttemptFiles(JobRecord job, IProgress<JobProgress>? progress, string what, bool reportDeleted)
    {
        var folder = Path.Combine(Path.GetDirectoryName(job.SourcePath)!, TemporaryOutputClassifier.FolderName);
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (!TemporaryOutputClassifier.TryParse(Path.GetFileName(file), out var jobId, out var attempt) || jobId != job.Id || attempt != job.Attempt) continue;
            try
            {
                File.Delete(file);
                if (reportDeleted) progress?.Report(JobProgress.Step($"Deleted {what}."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                progress?.Report(JobProgress.Step($"Could not delete {what} ({exception.Message}); run '{hints.Command("cleanup")}' later to remove {Path.GetFileName(file)}."));
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
            FailureCategories.ValidationFailed => $"Failed validation: {message}",
            FailureCategories.StagingFailed => $"Failed during the upload: {message}",
            FailureCategories.CrfSearchFailed => $"Failed during the CRF search: {message}",
            FailureCategories.EncodeFailed => $"Failed during encoding: {message}",
            FailureCategories.DownloadFailed => $"Failed during the download: {message}",
            FailureCategories.StageStalled => message,
            _ => $"Failed: {message}"
        }));
        if (logPath is not null) progress?.Report(JobProgress.Step($"Full log: {logPath}"));
        return new JobProcessingResult(job, exitCode, stored);
    }

    /// <summary>
    /// Ends the job as skipped: AV1 cannot meet the quality bar and the size cap together, or its audio cannot be copied. The
    /// fingerprint lets discovery check the video again once those settings change. A remote search has already cleaned its
    /// workspace on the way out; a skip before the search staged nothing in this run.
    /// </summary>
    private async Task<JobProcessingResult> SkipAsync(JobRecord job, string databasePath, AppSettings settings, string reason, IProgress<JobProgress>? progress, CancellationToken cancellationToken, string category = FailureCategories.NotWorthConverting)
    {
        job.Status = JobStatus.Skipped;
        job.FailureCategory = category;
        job.FailureMessage = reason;
        job.QualityFingerprint = QualityFingerprint.Compute(settings);
        job.CompletedUtc = DateTimeOffset.UtcNow;
        await jobs.UpdateAsync(databasePath, job, cancellationToken);
        progress?.Report(JobProgress.Outcome(JobTone.Warning, category == FailureCategories.UnsupportedAudio ? $"Skipped: {reason}" : $"Not worth converting: {reason}"));
        return new JobProcessingResult(job, ExitCode.Success, reason);
    }

    private static bool IsWithinRoot(string sourcePath, string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
        return sourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
