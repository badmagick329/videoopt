using VideoOptimiser.Application;
using VideoOptimiser.Cli;
using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Validate;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Features.Run;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests.Features.Run;

public sealed class JobProcessorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    public JobProcessorTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    private const string RestartLine = "The original changed after this video was stopped, so the conversion starts over.";

    /// <summary>
    /// The job also keeps the quality settings that chose its CRF, since calibrate counts a conversion only under the settings
    /// that judged it. ab-av1 printed no estimate, so the CRF line has none; the decode test is off, so the check line leaves it out.
    /// </summary>
    [Fact]
    public async Task ProcessAsyncTakesAQueuedJobToReadyToFinalize()
    {
        var source = await SourceAsync();
        var encoder = new FakeEncoder();
        var settings = Settings(new QualitySettings { MinimumVmaf = 95.5 });
        settings.Validation.DecodeTest = DecodeTestModes.None;
        var reported = new List<JobProgress>();

        var result = await Processor(encoder: encoder).ProcessAsync(DatabasePath, source, settings, force: false, new ListProgress(reported));

        result.ExitCode.Should().Be(ExitCode.Success);
        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize);
        result.Job.Crf.Should().Be(42);
        result.Job.ValidationPassed.Should().BeTrue();
        File.Exists(result.Job.OutputPath).Should().BeTrue();
        var stored = (await _repository.GetAsync(DatabasePath, result.Job.Id))!;
        // A job queued without a probe gets its facts from the run's own probe.
        stored.VideoFacts.Should().Be(new VideoFacts("h264", 1920, 1080, 10_000_000, 120, "original video data".Length));
        stored.QualityFingerprint.Should().Be(QualityFingerprint.Compute(settings));
        encoder.CallCount.Should().Be(1);
        Steps(reported).Should().Equal(
            "Finding the best CRF for VMAF 95.5 (2 test clips of 20s each).",
            "Chose CRF 42 in 0s.",
            "Encoding at CRF 42.",
            "Checking the output: streams, duration, savings.",
            "Ready to finalise: 100 B -> 50 B, 50.0% saved.");
    }

    [Fact]
    public async Task ProcessAsyncWritesTheContainerTheCodecFitsAndAsksForFlacOnlyForPcmStreams()
    {
        var source = await SourceAsync("clip.mov");
        var encoder = new FakeEncoder();

        var result = await Processor(encoder: encoder, probe: new H264Probe(audio: [new("aac"), new("pcm_s24le"), new("pcm_s16le")])).ProcessAsync(DatabasePath, source, Settings(), force: false);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        encoder.OutputPath.Should().EndWith(".encoding.mp4");
        encoder.Audio!.FlacOutputStreams.Should().Equal(1, 2);
        encoder.Audio.Dropped.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessAsyncAsksTheEncoderToDropAnAudioStreamFfmpegCannotIdentify()
    {
        var source = await SourceAsync("clip.mov");
        var encoder = new FakeEncoder();
        var reported = new List<JobProgress>();
        const string note = "Audio track 2 (apac) was left out because FFmpeg can't copy it. The other audio is kept.";

        var result = await Processor(encoder: encoder, probe: new H264Probe(audio: [new("aac"), new("apple_apac", "apac")])).ProcessAsync(DatabasePath, source, Settings(), force: false, new ListProgress(reported));

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        encoder.Audio!.Dropped.Should().Equal(new DroppedAudioTrack(1, "apac"));
        var stored = (await _repository.GetAsync(DatabasePath, result.Job.Id))!;
        (stored.Status, stored.ValidationWarnings.Count).Should().Be((JobStatus.ReadyToFinalize, 0));
        stored.Notes.Should().Equal(note);
        reported.TakeLast(2).Select(line => (line.Kind, line.Tone, line.Text)).Should().Equal(
            (JobProgressKind.Step, JobTone.Success, "Ready to finalise: 100 B -> 50 B, 50.0% saved."),
            (JobProgressKind.Detail, JobTone.None, $"Note: {note}"));
    }

    /// <summary>
    /// The remote session fails the job as soon as it is staged, so a skip proves nothing was uploaded. A video left with no
    /// audio is skipped even under drop, and even with --force: its AV1 would pass validation and replace the original.
    /// </summary>
    [Theory]
    [InlineData("skip", true, "FFmpeg can't copy audio track 2 (apac), and quality.unsupportedAudio is skip.")]
    [InlineData("drop", false, "FFmpeg can't copy audio track 1 (apac), its only audio, so the AV1 would have no sound.")]
    [InlineData("skip", false, "FFmpeg can't copy audio track 1 (apac), its only audio, so the AV1 would have no sound.")]
    public async Task AVideoWithAudioFfmpegCannotCopyIsSkippedBeforeItIsStagedWhenSkipIsSetOrNoAudioWouldBeLeft(string unsupportedAudio, bool withAac, string reason)
    {
        var source = await SourceAsync("spatial.mov");
        var settings = Settings(new QualitySettings { UnsupportedAudio = unsupportedAudio }, remote: true);
        AudioTrack[] audio = withAac ? [new("aac"), new("apple_apac", "apac")] : [new("unknown", "apac")];
        var reported = new List<JobProgress>();

        var result = await Processor(session: new UnavailableRemoteSession(), probe: new H264Probe(audio)).ProcessAsync(DatabasePath, source, settings, force: true, new ListProgress(reported));

        result.ExitCode.Should().Be(ExitCode.Success);
        var stored = (await _repository.GetAsync(DatabasePath, result.Job.Id))!;
        (stored.Status, stored.FailureCategory, stored.FailureMessage).Should().Be((JobStatus.Skipped, FailureCategories.UnsupportedAudio, reason));
        stored.QualityFingerprint.Should().Be(QualityFingerprint.Compute(settings));
        Steps(reported).Should().Equal($"Skipped: {reason}");
    }

    [Theory]
    [InlineData(JobSourceKind.Immich, "staging", JobStatus.ReadyToFinalize)]
    [InlineData(JobSourceKind.Immich, "elsewhere", JobStatus.Failed)]
    [InlineData(JobSourceKind.File, "staging", JobStatus.Failed)]
    public async Task ProcessAsyncAcceptsAStagedImmichOriginalOnlyUnderTheStagingDirectory(JobSourceKind kind, string stagingDirectory, JobStatus expected)
    {
        var staged = await SourceAsync(Path.Combine("staging", "asset-1", "PXL.mp4"));
        var settings = Settings(root: Path.Combine(_directory, "watched"));
        settings.Immich.StagingDirectory = Path.Combine(_directory, stagingDirectory);
        await _repository.CreateAsync(DatabasePath, new JobRecord
        {
            Id = Guid.NewGuid(), SourcePath = staged, SourceFingerprint = "fingerprint", Status = JobStatus.Queued,
            SourceKind = kind, ImmichAssetId = kind == JobSourceKind.Immich ? "asset-1" : null
        });

        var result = await Processor().ProcessAsync(DatabasePath, staged, settings, force: false);

        result.Job.Status.Should().Be(expected);
        if (expected == JobStatus.Failed) result.Job.FailureCategory.Should().Be("SourceOutsideWatchRoot");
    }

    [Fact]
    public async Task AMissingOriginalFailsItsJobSoItIsNotStuck()
    {
        var missing = Path.Combine(_directory, "gone.mp4");
        var job = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = missing, SourceFingerprint = "fingerprint", Status = JobStatus.Queued });

        var result = await Processor().ProcessAsync(DatabasePath, missing, new AppSettings { Database = new DatabaseSettings { Path = DatabasePath } }, force: false);

        result.ExitCode.Should().Be(ExitCode.ProcessingFailure);
        var stored = (await _repository.GetAsync(DatabasePath, job.Id))!;
        stored.Status.Should().Be(JobStatus.Failed);
        stored.FailureCategory.Should().Be("SourceMissing");
    }

    /// <summary>
    /// The original is read for its fingerprint after the exists check, so it can vanish, or fail to read, in between (VOPT-74 B21).
    /// That fails this job: a vanished file as missing, and anything else under the failure's own category. It used to escape
    /// the processor, end the run as a configuration error, and leave the videos after it unconverted.
    /// </summary>
    [Theory]
    [InlineData(true, FailureCategories.SourceMissing)]
    [InlineData(false, "ProcessingFailed")]
    public async Task AnOriginalThatCannotBeFingerprintedFailsItsJob(bool vanished, string category)
    {
        var source = await SourceAsync();
        var fingerprints = new ThrowingFingerprints(vanished ? new FileNotFoundException("Original does not exist.", source) : new IOException("disk read failed"));

        var result = await Processor(fingerprints: fingerprints).ProcessAsync(DatabasePath, source, Settings(), force: false);

        result.ExitCode.Should().Be(ExitCode.ProcessingFailure);
        var stored = (await _repository.ListAsync(DatabasePath, terminal: false)).Concat(await _repository.ListAsync(DatabasePath, terminal: true)).Single();
        (stored.Status, stored.FailureCategory).Should().Be((JobStatus.Failed, category));
        stored.FailureMessage.Should().Be(vanished ? $"Original does not exist: {source}" : "disk read failed");
    }

    [Fact]
    public async Task ProcessAsyncMarksTheJobInterruptedWhenCancelled()
    {
        var source = await SourceAsync("cancelled.mp4");

        var action = async () => await Processor(new CancellingCrfSearch()).ProcessAsync(DatabasePath, source, Settings(), force: false);

        await action.Should().ThrowAsync<OperationCanceledException>();
        (await _repository.ListAsync(DatabasePath, terminal: false)).Should().ContainSingle(job => job.Status == JobStatus.Interrupted);
    }

    /// <summary>The line names the cleanup command with the config the user typed, as the run's other advice does.</summary>
    [Fact]
    public async Task APartialOutputThatCannotBeDeletedSaysToRunCleanupWithTheTypedConfig()
    {
        var source = await SourceAsync("locked.mp4");
        var id = Guid.NewGuid();
        var partial = Path.Combine(_directory, ".video-optimiser", $".tmp.ab-av1-encoding.locked.{id:N}.1.encoding.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        await File.WriteAllTextAsync(partial, "partial av1");
        await _repository.CreateAsync(DatabasePath, new JobRecord
        {
            Id = id,
            SourcePath = source,
            SourceFingerprint = await Fingerprint(source),
            Status = JobStatus.Interrupted,
            ResumeStatus = JobStatus.Encoding,
            Attempt = 1,
            Crf = 42,
            OutputPath = Path.Combine(_directory, ".video-optimiser", $"locked.{id:N}.1.encoding.mp4")
        });
        var reported = new List<JobProgress>();

        // Windows refuses to delete a file another handle holds without sharing delete access.
        using (new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Processor(hints: new CommandHints("my.yaml")).ProcessAsync(DatabasePath, source, Settings(), force: false, new ListProgress(reported));
        }

        Steps(reported)[0].Should().EndWith("; run 'video-optimiser cleanup --config \"my.yaml\"' later to remove " + Path.GetFileName(partial) + ".");
    }

    [Fact]
    public async Task ResumingAnInterruptedLocalEncodeDeletesItsPartialOutputAndStartsANewAttempt()
    {
        var source = await SourceAsync("resumed.mp4");
        var id = Guid.NewGuid();
        // ab-av1 encodes under its own temporary name and renames only on success, so that is what a crash leaves.
        var partial = Path.Combine(_directory, ".video-optimiser", $".tmp.ab-av1-encoding.resumed.{id:N}.1.encoding.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        await File.WriteAllTextAsync(partial, "partial av1");
        await _repository.CreateAsync(DatabasePath, new JobRecord
        {
            Id = id,
            SourcePath = source,
            SourceFingerprint = await Fingerprint(source),
            Status = JobStatus.Interrupted,
            ResumeStatus = JobStatus.Encoding,
            Attempt = 1,
            Crf = 42,
            OutputPath = Path.Combine(_directory, ".video-optimiser", $"resumed.{id:N}.1.encoding.mp4")
        });
        var reported = new List<JobProgress>();

        var result = await Processor().ProcessAsync(DatabasePath, source, Settings(), force: false, new ListProgress(reported));

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize, result.Message);
        result.Job.Attempt.Should().Be(2);
        File.Exists(partial).Should().BeFalse();
        // The stored CRF skips the search, so there is no search line and no ab-av1 estimate to quote.
        Steps(reported).Should().Equal(
            "Deleted the unfinished AV1 from the stopped encode.",
            "Encoding at CRF 42.",
            "Checking the output: streams, duration, savings and decode test.",
            "Ready to finalise: 100 B -> 50 B, 50.0% saved.");
        reported.Last().Tone.Should().Be(JobTone.Success);
        reported.Where(line => line.Tone != JobTone.None).Should().ContainSingle("only the outcome line carries a tone");
    }

    [Fact]
    public async Task ResumingAnInterruptedLocalValidationReusesTheOutputWithoutEncoding()
    {
        var source = await SourceAsync("revalidated.mp4");
        var manifests = new OutputManifestStore();
        var id = Guid.NewGuid();
        var output = Path.Combine(_directory, ".video-optimiser", $"revalidated.{id:N}.1.encoding.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "av1");
        await manifests.SaveAsync(new OutputManifest { SourcePath = source, SourceFingerprint = "stale", OutputPath = output, Crf = 42, CreatedUtc = DateTimeOffset.UtcNow });
        await _repository.CreateAsync(DatabasePath, new JobRecord
        {
            Id = id,
            SourcePath = source,
            SourceFingerprint = await Fingerprint(source),
            Status = JobStatus.Interrupted,
            ResumeStatus = JobStatus.Validating,
            Attempt = 1,
            Crf = 42,
            OutputPath = output,
            ManifestPath = manifests.GetPath(output)
        });
        var encoder = new FakeEncoder();

        var result = await Processor(encoder: encoder, manifests: manifests).ProcessAsync(DatabasePath, source, Settings(), force: false);

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize, result.Message);
        result.Job.OutputPath.Should().Be(output);
        encoder.CallCount.Should().Be(0);
    }

    /// <summary>
    /// VOPT-138: the AV1 on this PC is of the original before it was edited, so it is deleted with its manifest, and the job
    /// searches and encodes the edited original under a new attempt. The restart line covers the delete.
    /// </summary>
    [Fact]
    public async Task ResumingALocalValidationAfterTheOriginalChangedDeletesTheOldAv1AndConvertsAgain()
    {
        var source = await SourceAsync("edited.mp4");
        var manifests = new OutputManifestStore();
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = await Fingerprint(source), Status = JobStatus.Interrupted, ResumeStatus = JobStatus.Validating, Attempt = 1, Crf = 30 };
        var output = await WriteOutputAsync(job, source);
        (job.OutputPath, job.ManifestPath) = (output, manifests.GetPath(output));
        await _repository.CreateAsync(DatabasePath, job);
        await EditAsync(source);
        var encoder = new FakeEncoder();
        var reported = new List<JobProgress>();

        var result = await Processor(encoder: encoder, manifests: manifests).ProcessAsync(DatabasePath, source, Settings(), force: false, new ListProgress(reported));

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize, result.Message);
        (result.Job.Attempt, result.Job.Crf, encoder.CallCount).Should().Be((2, 42, 1));
        (File.Exists(output), File.Exists(manifests.GetPath(output))).Should().Be((false, false));
        Steps(reported).Should().Equal(
            RestartLine,
            "Finding the best CRF for VMAF 95 (2 test clips of 20s each).",
            "Chose CRF 42 in 0s.",
            "Encoding at CRF 42.",
            "Checking the output: streams, duration, savings and decode test.",
            "Ready to finalise: 100 B -> 50 B, 50.0% saved.");
    }

    [Fact]
    public async Task RemoteConnectionFailureLeavesTheJobInterruptibleAndBoundToItsWorkspace()
    {
        var source = await SourceAsync("remote.mp4");
        var reported = new List<JobProgress>();

        var result = await Processor(session: new UnavailableRemoteSession()).ProcessAsync(DatabasePath, source, Settings(remote: true), force: false, new ListProgress(reported));

        Steps(reported).Should().Equal("Worker unreachable: Remote unavailable.");
        reported.Single().Tone.Should().Be(JobTone.Failure);
        result.Job.Status.Should().Be(JobStatus.Interrupted);
        result.Job.ResumeStatus.Should().Be(JobStatus.Staging);
        result.Job.ExecutionMode.Should().Be("remoteSsh");
        result.Job.RemoteHost.Should().Be("video-worker");
        result.Job.RemoteWorkspace.Should().Be($"/var/tmp/video-optimiser/{result.Job.Id:N}");
    }

    [Theory]
    [InlineData(CancellationStage.Downloading, JobStatus.Downloading)]
    [InlineData(CancellationStage.Validating, JobStatus.Validating)]
    public async Task CtrlCOnARemoteJobKeepsTheStageItStoppedInForTheRerun(CancellationStage cancellationStage, JobStatus expectedResumeStatus)
    {
        var source = await SourceAsync();
        var processor = Processor(session: new CancellingRemoteSession(cancellationStage), validator: cancellationStage == CancellationStage.Validating ? new CancellingValidator() : null);

        var action = async () => await processor.ProcessAsync(DatabasePath, source, Settings(remote: true), force: false);

        var interrupted = (await action.Should().ThrowAsync<JobInterruptedException>()).Which;
        interrupted.Stage.Should().Be(expectedResumeStatus);
        interrupted.Remote.Should().BeTrue();
        var job = (await _repository.ListAsync(DatabasePath, terminal: false)).Should().ContainSingle().Subject;
        job.Status.Should().Be(JobStatus.Interrupted);
        job.ResumeStatus.Should().Be(expectedResumeStatus);
    }

    [Fact]
    public async Task RerunningAnInterruptedRemoteEncodeReattachesToTheSameAttempt()
    {
        var source = await SourceAsync("reattach.mp4");
        var settings = Settings(remote: true);

        var stopped = async () => await Processor(session: new CancellingRemoteSession(CancellationStage.Encoding)).ProcessAsync(DatabasePath, source, settings, force: false);
        (await stopped.Should().ThrowAsync<JobInterruptedException>()).Which.Stage.Should().Be(JobStatus.Encoding);
        var rerun = new CancellingRemoteSession(CancellationStage.None);
        var result = await Processor(session: rerun).ProcessAsync(DatabasePath, source, settings, force: false);

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize);
        rerun.EncodeAttempts.Should().Equal(1);
        result.Job.Attempt.Should().Be(1);
    }

    /// <summary>
    /// The night case (UX redesign, assumption 1): a managed server is deleted with a job stopped on it, and the next run
    /// creates another at a new address with an empty disk. The job must resume there rather than fail as a changed
    /// configuration, keep its CRF, and upload and encode again, since the old workspace went with the old server.
    /// </summary>
    [Fact]
    public async Task AJobStoppedOnADeletedManagedServerFinishesOnANewOneWithoutSearchingAgain()
    {
        var source = await SourceAsync("swap.mp4");
        AppSettings SettingsOn(WorkerServer server)
        {
            var settings = Settings(remote: true);
            // The address is what the Hetzner lifecycle writes into the settings for the server it ensured.
            (settings.Processing.RemoteSsh.Lifecycle, settings.Processing.RemoteSsh.Host) = (RemoteLifecycleModes.Hetzner, server.Address);
            return settings;
        }
        var deleted = new WorkerServer("203.0.113.10");
        var created = new WorkerServer("203.0.113.20");

        var stopped = async () => await Processor(session: new ServerSession(deleted, CancellationStage.Downloading)).ProcessAsync(DatabasePath, source, SettingsOn(deleted), force: false);
        await stopped.Should().ThrowAsync<JobInterruptedException>();
        var result = await Processor(session: new ServerSession(created, CancellationStage.None)).ProcessAsync(DatabasePath, source, SettingsOn(created), force: false);

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize);
        result.Job.Crf.Should().Be(42);
        result.Job.RemoteHost.Should().Be(RemoteExecutionIdentity.ManagedHetzner);
        created.Searches.Should().Be(0);
        created.Uploads.Should().Be(1);
        created.EncodedAttempts.Should().Equal(1);
    }

    [Fact]
    public void AJobStoppedBeforeReachingAStageOfItsOwnKeepsItsEarlierResumePoint() =>
        JobProcessor.ResumePoint(JobStatus.Staging, JobStatus.Encoding).Should().Be(JobStatus.Encoding);

    /// <summary>
    /// A run creates a server only for a job that will reach a remote stage (VOPT-68). A lasting refusal (roots, rules, size)
    /// means no; a condition that can clear before the job runs means yes. The prediction writes nothing, so asking leaves no job behind.
    /// </summary>
    [Theory]
    [InlineData("eligible", true)]
    [InlineData("local mode", false)]
    [InlineData("below the rules' bitrate", false)]
    [InlineData("below the rules' bitrate, --force", true)]
    [InlineData("cannot save the minimum", false)]
    [InlineData("outside the watch roots", false)]
    [InlineData("unreadable by ffprobe", true)]
    [InlineData("not ready yet", true)]
    public async Task OnlyAJobThatPassesEveryLocalCheckNeedsTheWorker(string situation, bool expected)
    {
        var source = await SourceAsync("clip.mp4");
        var settings = Settings(
            remote: situation != "local mode",
            minimum: situation == "cannot save the minimum" ? "200MiB" : "0B",
            root: situation == "outside the watch roots" ? Path.Combine(_directory, "elsewhere") : null);
        IMediaProbe probe = situation == "unreadable by ffprobe" ? new FailingProbe() : new H264Probe(bitrate: situation.StartsWith("below", StringComparison.Ordinal) ? 500_000 : 10_000_000);
        var processor = Processor(session: new UnavailableRemoteSession(), probe: probe, files: situation == "not ready yet" ? new UnreadyFiles() : null);

        var needed = await processor.NeedsWorkerAsync(DatabasePath, source, settings, force: situation.EndsWith("--force", StringComparison.Ordinal));

        needed.Should().Be(expected);
        (await _repository.ListAsync(DatabasePath, terminal: false)).Should().BeEmpty();
    }

    /// <summary>A job whose finalisation was interrupted returns before any stage; one stopped in a remote stage still needs the worker.</summary>
    [Theory]
    [InlineData(JobStatus.Finalizing, false)]
    [InlineData(JobStatus.Encoding, true)]
    public async Task AnOpenJobNeedsTheWorkerOnlyWhenItStillHasARemoteStage(JobStatus resumeStatus, bool expected)
    {
        var source = await SourceAsync("open.mp4");
        var id = Guid.NewGuid();
        var job = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = id, SourcePath = source, Status = JobStatus.Queued, ExecutionMode = "remoteSsh", RemoteHost = "video-worker", RemoteWorkspace = $"/var/tmp/video-optimiser/{id:N}" });
        (job.Status, job.ResumeStatus) = (JobStatus.Interrupted, resumeStatus);
        await _repository.UpdateAsync(DatabasePath, job);

        (await Processor(session: new UnavailableRemoteSession()).NeedsWorkerAsync(DatabasePath, source, Settings(remote: true), force: false)).Should().Be(expected);
    }

    /// <summary>
    /// The remote session fails the job as soon as it is staged, so a skip here proves nothing was uploaded or searched.
    /// --force does not bypass the check; with it, an unknown bitrate leaves the file size to stand in for the stream.
    /// </summary>
    [Theory]
    [InlineData(false, "the video stream is about 143.1 MiB, so it cannot save 200MiB (savings.minimumBytesSaved).")]
    [InlineData(true, "the file is 19 B, so it cannot save 200MiB (savings.minimumBytesSaved).")]
    public async Task AVideoTooSmallToSaveTheMinimumIsSkippedBeforeItIsStagedOrSearched(bool unknownBitrate, string reason)
    {
        var source = await SourceAsync("small.mp4");
        var settings = Settings(remote: true, minimum: "200MiB");
        var processor = Processor(session: new UnavailableRemoteSession(), probe: new H264Probe(bitrate: unknownBitrate ? null : 10_000_000));
        var reported = new List<JobProgress>();

        var result = await processor.ProcessAsync(DatabasePath, source, settings, force: unknownBitrate, new ListProgress(reported));

        result.ExitCode.Should().Be(ExitCode.Success);
        var stored = (await _repository.GetAsync(DatabasePath, result.Job.Id))!;
        stored.Status.Should().Be(JobStatus.Skipped);
        stored.FailureCategory.Should().Be("NotWorthConverting");
        stored.FailureMessage.Should().Be(reason);
        stored.QualityFingerprint.Should().Be(QualityFingerprint.Compute(settings));
        Steps(reported).Should().Equal($"Not worth converting: {reason}");
    }

    /// <summary>
    /// The 143.1 MiB stream must save 100 MiB, so the search may keep no more than 30% of it. A search that finds nothing worth
    /// converting ends the job skipped, not failed, under the quality settings that judged it.
    /// </summary>
    [Fact]
    public async Task WhenTheMinimumSavingSetsTheCapTheSearchGetsItAndEveryLineNamesTheSaving()
    {
        var source = await SourceAsync("capped.mp4");
        var settings = Settings(new QualitySettings { MinimumVmaf = 97, MaxEncodedPercent = 80 }, minimum: "100MiB");
        var search = new NothingWorthwhileCrfSearch();
        var reported = new List<JobProgress>();

        var result = await Processor(search).ProcessAsync(DatabasePath, source, settings, force: false, new ListProgress(reported));

        search.MaxEncodedPercent.Should().Be(30);
        result.ExitCode.Should().Be(ExitCode.Success);
        var stored = (await _repository.GetAsync(DatabasePath, result.Job.Id))!;
        stored.Status.Should().Be(JobStatus.Skipped);
        stored.FailureCategory.Should().Be("NotWorthConverting");
        stored.FailureMessage.Should().Be("No CRF reached VMAF 97 while saving 100MiB (savings.minimumBytesSaved).");
        stored.QualityFingerprint.Should().Be(QualityFingerprint.Compute(settings));
        stored.CompletedUtc.Should().NotBeNull();
        // The header names the file, so the outcome line does not repeat it, and nothing follows the outcome line.
        Steps(reported).Should().Equal(
            "Finding the best CRF for VMAF 97 that saves 100MiB (2 test clips of 20s each).",
            $"Not worth converting: {stored.FailureMessage}");
        reported.Last().Tone.Should().Be(JobTone.Warning);
    }

    [Fact]
    public async Task ACrfSearchFailureKeepsTheFullLogAndNamesItInTheOutcomeAndTheStoredFailure()
    {
        var source = await SourceAsync("broken.mp4");
        var reported = new List<JobProgress>();

        var result = await Processor(new FailingLoggingCrfSearch()).ProcessAsync(DatabasePath, source, Settings(), force: false, new ListProgress(reported));

        var log = Path.Combine(_directory, ".video-optimiser", $"{result.Job.Id:N}.crf.log");
        result.ExitCode.Should().Be(ExitCode.ProcessingFailure);
        result.Job.Status.Should().Be(JobStatus.Failed);
        result.Job.QualityFingerprint.Should().BeNull("a failed search judged nothing about the quality bar");
        File.ReadAllLines(log).Should().Equal("first ab-av1 line", "Error: ffmpeg exploded");
        Steps(reported).Should().Equal(
            "Finding the best CRF for VMAF 95 (2 test clips of 20s each).",
            "Failed during the CRF search: ab-av1 crf-search failed with exit code 1: ffmpeg exploded",
            $"Full log: {log}");
        result.Job.FailureCategory.Should().Be("CrfSearchFailed");
        result.Job.FailureMessage.Should().Be($"ab-av1 crf-search failed with exit code 1: ffmpeg exploded Full log: {log}");
        TemporaryOutputClassifier.TryParse(Path.GetFileName(log), out _, out _).Should().BeFalse("cleanup must never treat a kept log as an abandoned output");
    }

    [Fact]
    public async Task AnEncodeFailureKeepsTheLogOfThatAttempt()
    {
        var source = await SourceAsync("broken.mp4");
        var reported = new List<JobProgress>();

        var result = await Processor(encoder: new FailingLoggingEncoder()).ProcessAsync(DatabasePath, source, Settings(), force: false, new ListProgress(reported));

        var log = Path.Combine(_directory, ".video-optimiser", $"{result.Job.Id:N}.encode-1.log");
        File.ReadAllLines(log).Should().Contain("Error: disk full");
        Steps(reported).TakeLast(2).Should().Equal("Failed during encoding: ab-av1 encode failed with exit code 1.", $"Full log: {log}");
        result.Job.FailureMessage.Should().EndWith($"Full log: {log}");
    }

    /// <summary>
    /// A stage stopped for its silence on the worker fails as <c>StageStalled</c>, not as the stage's own failure: its message
    /// already names the stage, so the outcome line is the message, then the kept log (VOPT-153).
    /// </summary>
    [Fact]
    public async Task AStalledRemoteStageFailsTheJobAsStalledAndNamesItsLog()
    {
        var source = await SourceAsync();
        const string message = "The encode on the worker wrote nothing to its log for 47m 00s, so it was stopped.";
        var log = Path.Combine(_directory, ".video-optimiser", "stalled.remote.log");
        var session = new ResumedWorkerSession { EncodeFailure = new RemoteStageStalledException(message, log) };
        var reported = new List<JobProgress>();

        var result = await Processor(session: session).ProcessAsync(DatabasePath, source, Settings(remote: true), force: false, new ListProgress(reported));

        result.ExitCode.Should().Be(ExitCode.ProcessingFailure);
        (result.Job.Status, result.Job.FailureCategory, result.Job.FailureMessage).Should().Be((JobStatus.Failed, FailureCategories.StageStalled, $"{message} Full log: {log}"));
        Steps(reported).TakeLast(2).Should().Equal(message, $"Full log: {log}");
        reported[^2].Tone.Should().Be(JobTone.Failure);
    }

    [Fact]
    public async Task AValidationFailureIsTheOutcomeWithItsFailuresJoined()
    {
        var source = await SourceAsync();
        var reported = new List<JobProgress>();

        var result = await Processor(validator: new ReportingValidator(new ValidationReport { Passed = false, Failures = ["Output is empty.", "Output video is not AV1."] })).ProcessAsync(DatabasePath, source, Settings(), force: false, new ListProgress(reported));

        result.ExitCode.Should().Be(ExitCode.ValidationFailure);
        (result.Job.Status, result.Job.FailureCategory).Should().Be((JobStatus.Failed, FailureCategories.ValidationFailed));
        Steps(reported).Last().Should().Be("Failed validation: Output is empty.; Output video is not AV1.");
        reported.Last().Tone.Should().Be(JobTone.Failure);
    }

    /// <summary>VOPT-117: a warned AV1 is ready, its warnings stored on the job and shown one per line in the warning tone.</summary>
    [Fact]
    public async Task WarningsLeaveTheJobReadyWithTheWarningsStoredAndShown()
    {
        var source = await SourceAsync();
        var reported = new List<JobProgress>();
        var report = new ValidationReport { Passed = true, Warnings = ["The AV1's video is 2 s shorter than the original's.", "The AV1 failed the decode test: bad frame"], SourceSizeBytes = 100, OutputSizeBytes = 50, PercentageSaved = 50 };

        var result = await Processor(validator: new ReportingValidator(report)).ProcessAsync(DatabasePath, source, Settings(), force: false, new ListProgress(reported));

        result.ExitCode.Should().Be(ExitCode.Success);
        var stored = (await _repository.GetAsync(DatabasePath, result.Job.Id))!;
        stored.Status.Should().Be(JobStatus.ReadyToFinalize);
        stored.ValidationWarnings.Should().Equal(report.Warnings);
        reported.TakeLast(3).Select(line => (line.Kind, line.Tone, line.Text)).Should().Equal(
            (JobProgressKind.Step, JobTone.Success, "Ready to finalise: 100 B -> 50 B, 50.0% saved. Check it before finalising:"),
            (JobProgressKind.Detail, JobTone.Warning, "The AV1's video is 2 s shorter than the original's."),
            (JobProgressKind.Detail, JobTone.Warning, "The AV1 failed the decode test: bad frame"));
    }

    /// <summary>VOPT-117: an AV1 that saves too little ends the job as a CRF search with nothing worthwhile does, and is deleted.</summary>
    [Fact]
    public async Task AnAv1NotWorthKeepingSkipsTheJobAndIsDeletedWithItsRecord()
    {
        var source = await SourceAsync();
        var settings = Settings();
        var reported = new List<JobProgress>();
        var report = new ValidationReport { Passed = false, NotWorthConverting = "the AV1 saves only 10 B, less than 1MiB (savings.minimumBytesSaved).", SourceSizeBytes = 100, OutputSizeBytes = 90 };

        var result = await Processor(validator: new ReportingValidator(report)).ProcessAsync(DatabasePath, source, settings, force: false, new ListProgress(reported));

        result.ExitCode.Should().Be(ExitCode.Success);
        var stored = (await _repository.GetAsync(DatabasePath, result.Job.Id))!;
        (stored.Status, stored.FailureCategory, stored.FailureMessage).Should().Be((JobStatus.Skipped, FailureCategories.NotWorthConverting, report.NotWorthConverting));
        stored.QualityFingerprint.Should().Be(QualityFingerprint.Compute(settings));
        File.Exists(stored.OutputPath).Should().BeFalse();
        File.Exists(new OutputManifestStore().GetPath(stored.OutputPath!)).Should().BeFalse();
        Steps(reported).Last().Should().Be("Not worth converting: the AV1 saves only 10 B, less than 1MiB (savings.minimumBytesSaved).");
        reported.Last().Tone.Should().Be(JobTone.Warning);
    }

    /// <summary>
    /// Staging hashes the original and needs the worker, and neither is wanted to fetch an encode that is already finished
    /// (VOPT-72 B10). The download resumes from the status it stopped in, and an encode the worker still holds is not run again.
    /// </summary>
    [Theory]
    [InlineData(JobStatus.Downloading, false)]
    [InlineData(JobStatus.Encoding, true)]
    public async Task AResumeWhoseEncodeFinishedOnTheWorkerGoesStraightToTheDownload(JobStatus stoppedIn, bool saysItFinished)
    {
        var source = await SourceAsync();
        var settings = Settings(remote: true);
        await StopRemoteJobAsync(source, settings, stoppedIn);
        var session = new ResumedWorkerSession { EncodeFinished = true };
        var reported = new List<JobProgress>();

        var result = await Processor(session: session).ProcessAsync(DatabasePath, source, settings, force: false, new ListProgress(reported));

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize, result.Message);
        session.Calls.Should().Equal("StopAndRemove", "HasFinishedEncode 1", "Retrieve 1", "Cleanup");
        result.Job.Attempt.Should().Be(1, "the finished encode is the one fetched, not a new attempt");
        Steps(reported).Should().Equal(saysItFinished
            ? ["The encode on the worker had already finished.", "Checking the output: streams, duration, savings and decode test.", "Ready to finalise: 100 B -> 50 B, 50.0% saved."]
            : ["Checking the output: streams, duration, savings and decode test.", "Ready to finalise: 100 B -> 50 B, 50.0% saved."]);
    }

    /// <summary>
    /// The worker answers no for an encode still running, so the encode stage follows it as before. A stop in the window of
    /// that question keeps the status it had: nothing was learned yet.
    /// </summary>
    [Fact]
    public async Task AResumeWhoseEncodeIsStillRunningFollowsItWithoutStagingAgainOrCountingANewAttempt()
    {
        var source = await SourceAsync();
        var settings = Settings(remote: true);
        await StopRemoteJobAsync(source, settings, JobStatus.Encoding);
        var session = new ResumedWorkerSession { EncodeFinished = false };

        var result = await Processor(session: session).ProcessAsync(DatabasePath, source, settings, force: false);

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize, result.Message);
        session.Calls.Should().Equal("StopAndRemove", "HasFinishedEncode 1", "Stage", "Encode 1", "Retrieve 1", "Cleanup");
        result.Job.Attempt.Should().Be(1);
    }

    /// <summary>
    /// A rerun that finds the encode finished records Downloading before anything can stop it, so Ctrl+C or a worker that stops
    /// answering during the download says the finished encode waits on the worker, not that an encode is still running (VOPT-86).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStopWhileFetchingAnEncodeThatFinishedWhileStoppedKeepsTheDownloadAsItsResumePoint(bool unreachable)
    {
        var source = await SourceAsync();
        var settings = Settings(remote: true);
        await StopRemoteJobAsync(source, settings, JobStatus.Encoding);
        var session = new ResumedWorkerSession { EncodeFinished = true, RetrieveFailure = unreachable ? new RemoteConnectionException("SSH connection failed: timed out.") : new OperationCanceledException() };

        JobProcessingResult? result = null;
        var action = async () => result = await Processor(session: session).ProcessAsync(DatabasePath, source, settings, force: false);

        if (unreachable)
        {
            await action.Should().NotThrowAsync();
            result!.Job.FailureCategory.Should().Be(FailureCategories.RemoteUnavailable);
        }
        else (await action.Should().ThrowAsync<JobInterruptedException>()).Which.Stage.Should().Be(JobStatus.Downloading);
        var stored = (await _repository.ListAsync(DatabasePath, terminal: false)).Should().ContainSingle().Subject;
        (stored.Status, stored.ResumeStatus).Should().Be((JobStatus.Interrupted, JobStatus.Downloading));
    }

    /// <summary>
    /// The AV1 is already on this PC, so checking it needs no worker: nothing is asked of the session, and the run is not made to
    /// create a server for it (VOPT-89). Its workspace is left for the sweep of jobs that are no longer open. Once the original
    /// is edited that AV1 is of the old video (VOPT-138): it is deleted, and the job needs the worker to start again there, its
    /// old workspace stopped and removed first.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResumeFromValidatingWithTheAv1OnThisPcNeedsNoWorkerUnlessTheOriginalChanged(bool edited)
    {
        var source = await SourceAsync();
        var settings = Settings(remote: true);
        var job = await StopRemoteJobAsync(source, settings, JobStatus.Validating);
        var output = await WriteOutputAsync(job, source);
        var manifests = new OutputManifestStore();
        job.OutputPath = output;
        job.ManifestPath = manifests.GetPath(output);
        await _repository.UpdateAsync(DatabasePath, job);
        if (edited) await EditAsync(source);
        var session = new ResumedWorkerSession();
        var processor = Processor(session: session);

        (await processor.NeedsWorkerAsync(DatabasePath, source, settings, force: false)).Should().Be(edited);
        var result = await processor.ProcessAsync(DatabasePath, source, settings, force: false);

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize, result.Message);
        if (!edited)
        {
            session.Calls.Should().BeEmpty();
            return;
        }
        session.Calls.Should().Equal("StopAndRemove", "StopAndRemove", "Stage", "Search", "Encode 2", "Retrieve 2", "Cleanup");
        session.Removals[0].Should().Equal(job.Id);
        (File.Exists(output), File.Exists(manifests.GetPath(output))).Should().Be((false, false));
        result.Job.OutputPath.Should().NotBe(output);
    }

    /// <summary>
    /// VOPT-138: an original edited while its job was stopped on the worker, such as one re-tagged with another date, makes
    /// everything there of the old video. Its workspace is stopped and removed before anything else is asked of the worker, so
    /// a finished encode is not downloaded, a running one is not followed, and a search stopped midway is not picked up; the
    /// job uploads, searches and encodes again under a new attempt.
    /// </summary>
    [Theory]
    [InlineData(JobStatus.Encoding, true, 2)]
    [InlineData(JobStatus.Encoding, false, 2)]
    [InlineData(JobStatus.CrfSearching, false, 1)]
    public async Task AResumeAfterTheOriginalChangedStopsTheOldWorkOnTheWorkerAndStartsAgain(JobStatus stoppedIn, bool encodeFinished, int attempt)
    {
        var source = await SourceAsync();
        var settings = Settings(remote: true);
        var job = await StopRemoteJobAsync(source, settings, stoppedIn);
        await EditAsync(source);
        var session = new ResumedWorkerSession { EncodeFinished = encodeFinished };
        var reported = new List<JobProgress>();

        var result = await Processor(session: session).ProcessAsync(DatabasePath, source, settings, force: false, new ListProgress(reported));

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize, result.Message);
        session.Calls.Should().Equal("StopAndRemove", "StopAndRemove", "Stage", "Search", $"Encode {attempt}", $"Retrieve {attempt}", "Cleanup");
        session.Removals[0].Should().Equal(job.Id);
        result.Job.Attempt.Should().Be(attempt);
        result.Job.SourceFingerprint.Should().Be(await Fingerprint(source));
        Steps(reported)[0].Should().Be(RestartLine);
    }

    /// <summary>A job with no stored fingerprint has nothing to compare, so its resume reattaches as before.</summary>
    [Fact]
    public async Task AResumeWithNoStoredFingerprintReattachesWithoutStartingAgain()
    {
        var source = await SourceAsync();
        var settings = Settings(remote: true);
        var job = await StopRemoteJobAsync(source, settings, JobStatus.Encoding);
        job.SourceFingerprint = string.Empty;
        await _repository.UpdateAsync(DatabasePath, job);
        var session = new ResumedWorkerSession { EncodeFinished = true };
        var reported = new List<JobProgress>();

        var result = await Processor(session: session).ProcessAsync(DatabasePath, source, settings, force: false, new ListProgress(reported));

        session.Calls.Should().Equal("StopAndRemove", "HasFinishedEncode 1", "Retrieve 1", "Cleanup");
        session.Removals[0].Should().BeEmpty("the sweep names only ended jobs");
        result.Job.Attempt.Should().Be(1);
        Steps(reported).Should().NotContain(RestartLine);
    }

    /// <summary>
    /// A downloaded Immich copy that <c>cleanup</c> deleted comes back with the same bytes but a new last-write time, so a
    /// different fingerprint. Restaging stores the new one, so the stopped encode is fetched rather than thrown away as the
    /// encode of an edited original. The processor gets the job from the database, as the run does.
    /// </summary>
    [Fact]
    public async Task AnImmichCopyDownloadedAgainResumesTheEncodeItWasStoppedIn()
    {
        var settings = Settings(remote: true);
        settings.Immich.StagingDirectory = Path.Combine(_directory, "staging");
        var immich = new InMemoryImmich(new DateTimeOffset(2024, 3, 4, 12, 0, 0, TimeSpan.Zero));
        var asset = immich.Add("PXL.mp4", "original video data");
        var source = await SourceAsync(Path.Combine("staging", asset.Id, "PXL.mp4"));
        File.SetLastWriteTimeUtc(source, new DateTime(2024, 3, 5, 0, 0, 0, DateTimeKind.Utc));
        var job = await StopRemoteJobAsync(source, settings, JobStatus.Encoding);
        (job.SourceKind, job.ImmichAssetId, job.ImmichChecksum) = (JobSourceKind.Immich, asset.Id, asset.Checksum);
        await _repository.UpdateAsync(DatabasePath, job);
        File.Delete(source);
        var session = new ResumedWorkerSession { EncodeFinished = true };

        var restage = await new ImmichRestaging(immich, _repository, new FileFingerprintService()).RestageMissingAsync(DatabasePath, settings, [job]);
        var result = await Processor(session: session).ProcessAsync(DatabasePath, source, settings, force: false);

        restage.Failed.Should().BeEmpty();
        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize, result.Message);
        session.Calls.Should().Equal("StopAndRemove", "HasFinishedEncode 1", "Retrieve 1", "Cleanup");
        result.Job.Attempt.Should().Be(1);
    }

    /// <summary>
    /// The first session a run opens on a worker ends what dropped videos left running there (VOPT-90): once, not per video,
    /// and told only of the jobs the database holds as ended, here a dropped and a failed one. A stopped job, one still running
    /// and any job the database does not know are not in the list, so their workspaces stay.
    /// </summary>
    [Fact]
    public async Task TheFirstSessionOnAWorkerSweepsOnceAndNamesOnlyTheJobsThatEnded()
    {
        var settings = Settings(remote: true);
        await StopRemoteJobAsync(await SourceAsync("stopped.mp4"), settings, JobStatus.Encoding);
        var dropped = await StopRemoteJobAsync(await SourceAsync("dropped.mp4"), settings, JobStatus.Encoding);
        dropped.Status = JobStatus.Cancelled;
        await _repository.UpdateAsync(DatabasePath, dropped);
        var failed = await StopRemoteJobAsync(await SourceAsync("failed.mp4"), settings, JobStatus.Encoding);
        failed.Status = JobStatus.Failed;
        await _repository.UpdateAsync(DatabasePath, failed);
        var session = new ResumedWorkerSession();
        var processor = Processor(session: session);

        await processor.ProcessAsync(DatabasePath, await SourceAsync("a.mp4"), settings, force: false);
        await processor.ProcessAsync(DatabasePath, await SourceAsync("b.mp4"), settings, force: false);

        session.Removals.Should().ContainSingle().Which.Should().BeEquivalentTo(new[] { dropped.Id, failed.Id });
        session.Calls.Take(2).Should().Equal("StopAndRemove", "Stage");
    }

    /// <summary>The sweep is housekeeping: a worker that cannot answer it fails the video's own stage, not the video before that.</summary>
    [Fact]
    public async Task AFailedSweepDoesNotFailTheVideo()
    {
        var source = await SourceAsync();
        var session = new ResumedWorkerSession { RemovalFailure = new RemoteConnectionException("SSH connection failed: timed out.") };

        var result = await Processor(session: session).ProcessAsync(DatabasePath, source, Settings(remote: true), force: false);

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize, result.Message);
    }

    /// <summary>A search this command joined midway has no start to time it from, so its line gives no duration.</summary>
    [Fact]
    public async Task ASearchTheCommandJoinedMidwayNamesNoDuration()
    {
        var source = await SourceAsync();
        var reported = new List<JobProgress>();
        var session = new ResumedWorkerSession { SearchDuration = null };

        await Processor(session: session).ProcessAsync(DatabasePath, source, Settings(remote: true), force: false, new ListProgress(reported));

        Steps(reported).Should().Contain("Chose CRF 42.");
    }

    /// <summary>
    /// Stops a job on a worker in <paramref name="stoppedIn"/>, as an earlier run left it: with the original's fingerprint as
    /// that run read it, its CRF chosen and one attempt made, or, stopped in the search, neither.
    /// </summary>
    private async Task<JobRecord> StopRemoteJobAsync(string source, AppSettings settings, JobStatus stoppedIn)
    {
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = await Fingerprint(source), Status = JobStatus.Queued };
        JobCompatibility.Apply(job, JobCompatibility.ExpectedBinding(job.Id, settings));
        job = await _repository.CreateAsync(DatabasePath, job);
        var searched = stoppedIn != JobStatus.CrfSearching;
        (job.Status, job.ResumeStatus, job.Crf, job.Attempt) = (JobStatus.Interrupted, stoppedIn, searched ? 42 : null, searched ? 1 : 0);
        await _repository.UpdateAsync(DatabasePath, job);
        return job;
    }

    private static Task<string> Fingerprint(string source) => new FileFingerprintService().CreateAsync(source);

    /// <summary>Edits the original as another app would, such as one fixing its date or place.</summary>
    private static Task EditAsync(string source) => File.WriteAllTextAsync(source, "original video data, re-tagged");

    /// <summary>The AV1 and manifest of attempt 1 of <paramref name="job"/>, on this PC.</summary>
    private async Task<string> WriteOutputAsync(JobRecord job, string source)
    {
        var output = Path.Combine(_directory, ".video-optimiser", $"{Path.GetFileNameWithoutExtension(source)}.{job.Id:N}.1.encoding.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "av1");
        await new OutputManifestStore().SaveAsync(new OutputManifest { SourcePath = source, SourceFingerprint = job.SourceFingerprint, OutputPath = output, Crf = 42, CreatedUtc = DateTimeOffset.UtcNow });
        return output;
    }

    /// <summary>An original of 19 B at <paramref name="name"/>, a path under the test's directory.</summary>
    private async Task<string> SourceAsync(string name = "movie.mp4")
    {
        var source = Path.Combine(_directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "original video data");
        return source;
    }

    /// <param name="minimum">savings.minimumBytesSaved.</param>
    /// <param name="root">The one watch root; null is the test's directory.</param>
    private AppSettings Settings(QualitySettings? quality = null, bool remote = false, string minimum = "0B", string? root = null) => new()
    {
        Database = new DatabaseSettings { Path = DatabasePath },
        Savings = new SavingsSettings { MinimumBytesSaved = minimum },
        Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = root ?? _directory }] },
        Eligibility = new EligibilitySettings { Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "1Mbps", MinimumFileSize = "1B" }] },
        Quality = quality ?? new QualitySettings(),
        Processing = remote ? new ProcessingSettings { Mode = ProcessingModes.RemoteSsh, RemoteSsh = new RemoteSshSettings { Host = "video-worker" } } : new ProcessingSettings()
    };

    /// <summary>The real processor over fakes; <paramref name="session"/>, when given, replaces the local search and encoder.</summary>
    private JobProcessor Processor(
        ICrfSearchClient? search = null, IVideoEncoder? encoder = null, IProcessingSession? session = null, IOutputValidationService? validator = null,
        IMediaProbe? probe = null, IFileReadinessService? files = null, OutputManifestStore? manifests = null, IFileFingerprintService? fingerprints = null, ICommandHints? hints = null) => new(
        _repository,
        files ?? new ReadableFiles(),
        _ => probe ?? new H264Probe(),
        session is null ? new ProcessingSessionFactory(_ => search ?? new FixedCrfSearch(), _ => encoder ?? new FakeEncoder(), new UnexpectedExternalProcessRunner()) : new FixedSessionFactory(session),
        fingerprints ?? new FileFingerprintService(),
        manifests ?? new OutputManifestStore(),
        validator ?? new PassingValidator(),
        new AbAv1ProgressTranslator(),
        hints ?? new CommandHints(null));

    private static string[] Steps(IEnumerable<JobProgress> reported) => [.. reported.Where(line => line.Kind == JobProgressKind.Step).Select(line => line.Text)];

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class ThrowingFingerprints(Exception failure) : IFileFingerprintService
    {
        public Task<string> CreateAsync(string path, CancellationToken cancellationToken = default) => throw failure;
        public Task<string> CreateAsync(string path, string fingerprintPath, CancellationToken cancellationToken = default) => throw failure;
    }

    private sealed class FailingLoggingCrfSearch : ICrfSearchClient
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) => [];
        public Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(JobProgress.Tool("first ab-av1 line"));
            progress?.Report(JobProgress.Tool("Error: ffmpeg exploded"));
            throw new InvalidOperationException("ab-av1 crf-search failed with exit code 1: ffmpeg exploded");
        }
    }

    private sealed class FailingLoggingEncoder : IVideoEncoder
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int crf, QualitySettings settings, AudioPlan audio) => [];
        public Task<EncodeResult> EncodeAsync(string inputPath, string outputPath, int crf, QualitySettings settings, AudioPlan audio, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(JobProgress.Tool("Error: disk full"));
            throw new InvalidOperationException("ab-av1 encode failed with exit code 1.");
        }
    }

    private sealed class ReportingValidator(ValidationReport report) : IOutputValidationService
    {
        public Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(report);
    }

    private sealed class UnreadyFiles : IFileReadinessService
    {
        public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new FileReadinessResult(false, "it is still being written"));
    }

    private sealed class FailingProbe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) => throw new MediaProbeException("moov atom not found");
    }

    /// <summary>Keeps the cap ab-av1 would be given, then ends as a search where no attempt met the VMAF.</summary>
    private sealed class NothingWorthwhileCrfSearch : ICrfSearchClient
    {
        public int? MaxEncodedPercent { get; private set; }
        public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) => [];
        public Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            MaxEncodedPercent = settings.MaxEncodedPercent;
            throw new NoWorthwhileCrfException([new CrfAttempt(30, 96.1, 25)], settings);
        }
    }

    private sealed class CancellingCrfSearch : ICrfSearchClient
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) => [];
        public Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => throw new OperationCanceledException();
    }

    private sealed class UnavailableRemoteSession : IProcessingSession
    {
        public bool IsRemote => true;
        public string ExecutionMode => "remoteSsh";
        public string? RemoteHost => "video-worker";
        public string? RemoteWorkspace => null;
        public Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default) => throw new RemoteConnectionException("Remote unavailable.");
        public Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, AudioPlan audio, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> HasFinishedEncodeAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAndRemoveWorkspacesAsync(IReadOnlySet<Guid> jobIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public enum CancellationStage
    {
        None,
        Encoding,
        Downloading,
        Validating
    }

    private sealed class CancellingRemoteSession(CancellationStage cancellationStage) : IProcessingSession
    {
        public List<int> EncodeAttempts { get; } = [];
        public bool IsRemote => true;
        public string ExecutionMode => "remoteSsh";
        public string? RemoteHost => "video-worker";
        public string? RemoteWorkspace => null;
        public Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new CrfSearchResult(42, string.Empty, string.Empty, TimeSpan.Zero));

        public Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, AudioPlan audio, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            EncodeAttempts.Add(attempt);
            if (cancellationStage == CancellationStage.Encoding) throw new OperationCanceledException();
            return Task.FromResult(new ProcessingStageResult(outputPath, TimeSpan.Zero));
        }

        public Task<bool> HasFinishedEncodeAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public async Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default)
        {
            if (cancellationStage == CancellationStage.Downloading) throw new OperationCanceledException();
            await File.WriteAllTextAsync(outputPath, "av1", cancellationToken);
        }

        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAndRemoveWorkspacesAsync(IReadOnlySet<Guid> jobIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>One server's disk: what was uploaded to it and what it encoded. A new server starts empty.</summary>
    private sealed class WorkerServer(string address)
    {
        public string Address { get; } = address;
        public int Uploads { get; set; }
        public int Searches { get; set; }
        public List<int> EncodedAttempts { get; } = [];
    }

    /// <summary>
    /// A session on one <see cref="WorkerServer"/>. Each stage needs what the one before left on that server's disk, as
    /// ab-av1 needs the uploaded source and the download needs the encoded output, and it stops with Ctrl+C in the given stage.
    /// </summary>
    private sealed class ServerSession(WorkerServer server, CancellationStage stopIn) : IProcessingSession
    {
        public bool IsRemote => true;
        public string ExecutionMode => "remoteSsh";
        public string? RemoteHost => server.Address;
        public string? RemoteWorkspace => null;

        public Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default)
        {
            server.Uploads++;
            return Task.CompletedTask;
        }

        public Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (server.Uploads == 0) throw new InvalidOperationException("No source on this server.");
            server.Searches++;
            return Task.FromResult(new CrfSearchResult(42, string.Empty, string.Empty, TimeSpan.Zero));
        }

        public Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, AudioPlan audio, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (server.Uploads == 0) throw new InvalidOperationException("No source on this server.");
            if (stopIn == CancellationStage.Encoding) throw new OperationCanceledException();
            server.EncodedAttempts.Add(attempt);
            return Task.FromResult(new ProcessingStageResult(outputPath, TimeSpan.Zero));
        }

        public Task<bool> HasFinishedEncodeAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) => Task.FromResult(server.EncodedAttempts.Contains(attempt));

        public async Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default)
        {
            if (!server.EncodedAttempts.Contains(attempt)) throw new InvalidOperationException($"No output of attempt {attempt} on this server.");
            if (stopIn == CancellationStage.Downloading) throw new OperationCanceledException();
            await File.WriteAllTextAsync(outputPath, "av1", cancellationToken);
        }

        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAndRemoveWorkspacesAsync(IReadOnlySet<Guid> jobIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellingValidator : IOutputValidationService
    {
        public Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default) => throw new OperationCanceledException();
    }
}
