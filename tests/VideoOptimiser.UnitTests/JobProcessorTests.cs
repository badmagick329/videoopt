using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class JobProcessorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public JobProcessorTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ProcessAsyncPersistsAReadyToFinalizeJobAndReusesIt()
    {
        var source = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var encoder = new FakeEncoder();
        var processor = new JobProcessor(
            repository,
            new ReadableFiles(),
            _ => new H264Probe(),
            new ProcessingSessionFactory(_ => new FixedCrfSearch(), _ => encoder, new UnexpectedExternalProcessRunner()),
            new FileFingerprintService(),
            new OutputManifestStore(),
            new PassingValidator());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Eligibility = Rules()
        };

        var first = await processor.ProcessAsync(settings.Database.Path, source, settings, force: false);
        var reported = new List<JobProgress>();
        var second = await processor.ProcessAsync(settings.Database.Path, source, settings, force: false, new SynchronousProgress(reported.Add));

        first.ExitCode.Should().Be(ExitCode.Success);
        first.Job.Status.Should().Be(JobStatus.ReadyToFinalize);
        first.Job.Crf.Should().Be(42);
        first.Job.ValidationPassed.Should().BeTrue();
        File.Exists(first.Job.OutputPath).Should().BeTrue();
        second.Job.Id.Should().Be(first.Job.Id);
        encoder.CallCount.Should().Be(1);
        Steps(reported).Should().Equal($"Job {first.Job.Id:N} is already ready to finalise.");
        reported.Single().Tone.Should().Be(JobTone.Success);
    }

    [Theory]
    [InlineData("clip.mov", new[] { "aac", "pcm_s24le", "pcm_s16le" }, ".mp4", new[] { 1, 2 })]
    [InlineData("clip.avi", new string[0], ".mkv", new int[0])]
    public async Task ProcessAsyncWritesTheContainerTheCodecFitsAndAsksForFlacOnlyForPcmStreams(string sourceName, string[] audioCodecs, string expectedExtension, int[] expectedFlac)
    {
        var source = Path.Combine(_directory, sourceName);
        await File.WriteAllTextAsync(source, "original video data");
        var encoder = new FakeEncoder();
        var processor = new JobProcessor(
            new SqliteJobRepository(new SqliteDatabaseInitializer()),
            new ReadableFiles(),
            _ => new H264Probe(audioCodecs: audioCodecs),
            new ProcessingSessionFactory(_ => new FixedCrfSearch(), _ => encoder, new UnexpectedExternalProcessRunner()),
            new FileFingerprintService(),
            new OutputManifestStore(),
            new PassingValidator());
        var settings = Settings("jobs.db");

        var result = await processor.ProcessAsync(settings.Database.Path, source, settings, force: false);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        encoder.OutputPath.Should().EndWith(".encoding" + expectedExtension);
        encoder.FlacAudioStreams.Should().Equal(expectedFlac);
    }

    [Theory]
    [InlineData(JobSourceKind.Immich, "staging", JobStatus.ReadyToFinalize)]
    [InlineData(JobSourceKind.Immich, "elsewhere", JobStatus.Failed)]
    [InlineData(JobSourceKind.File, "staging", JobStatus.Failed)]
    public async Task ProcessAsyncAcceptsAStagedImmichOriginalOnlyUnderTheStagingDirectory(JobSourceKind kind, string stagingDirectory, JobStatus expected)
    {
        var staged = Path.Combine(_directory, "staging", "asset-1", "PXL.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        await File.WriteAllTextAsync(staged, "original video data");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var processor = new JobProcessor(
            repository,
            new ReadableFiles(),
            _ => new H264Probe(),
            new ProcessingSessionFactory(_ => new FixedCrfSearch(), _ => new FakeEncoder(), new UnexpectedExternalProcessRunner()),
            new FileFingerprintService(),
            new OutputManifestStore(),
            new PassingValidator());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = Path.Combine(_directory, "watched") }] },
            Immich = new ImmichSettings { StagingDirectory = Path.Combine(_directory, stagingDirectory) },
            Eligibility = Rules()
        };
        await repository.CreateAsync(settings.Database.Path, new JobRecord
        {
            Id = Guid.NewGuid(), SourcePath = staged, SourceFingerprint = "fingerprint", Status = JobStatus.Queued,
            SourceKind = kind, ImmichAssetId = kind == JobSourceKind.Immich ? "asset-1" : null
        });

        var result = await processor.ProcessAsync(settings.Database.Path, staged, settings, force: false);

        result.Job.Status.Should().Be(expected);
        if (expected == JobStatus.Failed) result.Job.FailureCategory.Should().Be("SourceOutsideWatchRoot");
    }

    [Theory]
    [InlineData(JobStatus.Queued, JobStatus.Failed)]
    [InlineData(JobStatus.Interrupted, JobStatus.Failed)]
    [InlineData(JobStatus.ReadyToFinalize, JobStatus.ReadyToFinalize)]
    public async Task AMissingOriginalFailsItsQueuedOrInterruptedJobSoItIsNotStuck(JobStatus status, JobStatus expected)
    {
        var missing = Path.Combine(_directory, "gone.mp4");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var processor = new JobProcessor(
            repository,
            new ReadableFiles(),
            _ => new H264Probe(),
            new ProcessingSessionFactory(_ => new FixedCrfSearch(), _ => new FakeEncoder(), new UnexpectedExternalProcessRunner()),
            new FileFingerprintService(),
            new OutputManifestStore(),
            new PassingValidator());
        var databasePath = Path.Combine(_directory, "jobs.db");
        var job = await repository.CreateAsync(databasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = missing, SourceFingerprint = "fingerprint", Status = status });

        var result = await processor.ProcessAsync(databasePath, missing, new AppSettings { Database = new DatabaseSettings { Path = databasePath } }, force: false);

        result.ExitCode.Should().Be(ExitCode.ProcessingFailure);
        var stored = (await repository.GetAsync(databasePath, job.Id))!;
        stored.Status.Should().Be(expected);
        if (expected == JobStatus.Failed) stored.FailureCategory.Should().Be("SourceMissing");
    }

    [Fact]
    public async Task ProcessAsyncMarksTheJobInterruptedWhenCancelled()
    {
        var source = Path.Combine(_directory, "cancelled.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var databasePath = Path.Combine(_directory, "cancelled.db");
        var processor = new JobProcessor(
            new SqliteJobRepository(new SqliteDatabaseInitializer()),
            new ReadableFiles(),
            _ => new H264Probe(),
            new ProcessingSessionFactory(_ => new CancellingCrfSearch(), _ => new FakeEncoder(), new UnexpectedExternalProcessRunner()),
            new FileFingerprintService(),
            new OutputManifestStore(),
            new PassingValidator());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = databasePath },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Eligibility = Rules()
        };

        var action = async () => await processor.ProcessAsync(databasePath, source, settings, force: false);

        await action.Should().ThrowAsync<OperationCanceledException>();
        (await new SqliteJobRepository(new SqliteDatabaseInitializer()).ListAsync(databasePath, terminal: false)).Should().ContainSingle(job => job.Status == JobStatus.Interrupted);
    }

    [Fact]
    public async Task ResumingAnInterruptedLocalEncodeDeletesItsPartialOutputAndStartsANewAttempt()
    {
        var source = Path.Combine(_directory, "resumed.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var databasePath = Path.Combine(_directory, "resumed.db");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var id = Guid.NewGuid();
        // ab-av1 encodes under its own temporary name and renames only on success, so that is what a crash leaves.
        var partial = Path.Combine(_directory, ".video-optimiser", $".tmp.ab-av1-encoding.resumed.{id:N}.1.encoding.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        await File.WriteAllTextAsync(partial, "partial av1");
        await repository.CreateAsync(databasePath, new JobRecord
        {
            Id = id,
            SourcePath = source,
            SourceFingerprint = "stale",
            Status = JobStatus.Interrupted,
            ResumeStatus = JobStatus.Encoding,
            Attempt = 1,
            Crf = 42,
            OutputPath = Path.Combine(_directory, ".video-optimiser", $"resumed.{id:N}.1.encoding.mp4")
        });
        var processor = new JobProcessor(
            repository,
            new ReadableFiles(),
            _ => new H264Probe(),
            new ProcessingSessionFactory(_ => new FixedCrfSearch(), _ => new FakeEncoder(), new UnexpectedExternalProcessRunner()),
            new FileFingerprintService(),
            new OutputManifestStore(),
            new PassingValidator());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = databasePath },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Eligibility = Rules()
        };
        var reported = new List<JobProgress>();

        var result = await processor.ProcessAsync(databasePath, source, settings, force: false, new SynchronousProgress(reported.Add));

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize, result.Message);
        result.Job.Attempt.Should().Be(2);
        File.Exists(partial).Should().BeFalse();
        // The stored CRF skips the search, so there is no search line and no ab-av1 estimate to quote.
        Steps(reported).Should().Equal(
            "Deleted the partial output of interrupted attempt 1.",
            "Encoding at CRF 42...",
            "Checking the output: streams, duration, savings and decode test...",
            "Ready to finalise: 100 B -> 50 B, 50.0% saved.");
        reported.Last().Tone.Should().Be(JobTone.Success);
        reported.Where(line => line.Tone != JobTone.None).Should().ContainSingle("only the outcome line carries a tone");
    }

    [Fact]
    public async Task ResumingAnInterruptedLocalValidationReusesTheOutputWithoutEncoding()
    {
        var source = Path.Combine(_directory, "revalidated.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var databasePath = Path.Combine(_directory, "revalidated.db");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var manifests = new OutputManifestStore();
        var id = Guid.NewGuid();
        var output = Path.Combine(_directory, ".video-optimiser", $"revalidated.{id:N}.1.encoding.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "av1");
        await manifests.SaveAsync(new OutputManifest { SourcePath = source, SourceFingerprint = "stale", OutputPath = output, Crf = 42, CreatedUtc = DateTimeOffset.UtcNow });
        await repository.CreateAsync(databasePath, new JobRecord
        {
            Id = id,
            SourcePath = source,
            SourceFingerprint = "stale",
            Status = JobStatus.Interrupted,
            ResumeStatus = JobStatus.Validating,
            Attempt = 1,
            Crf = 42,
            OutputPath = output,
            ManifestPath = manifests.GetPath(output)
        });
        var encoder = new FakeEncoder();
        var processor = new JobProcessor(
            repository,
            new ReadableFiles(),
            _ => new H264Probe(),
            new ProcessingSessionFactory(_ => new FixedCrfSearch(), _ => encoder, new UnexpectedExternalProcessRunner()),
            new FileFingerprintService(),
            manifests,
            new PassingValidator());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = databasePath },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Eligibility = Rules()
        };

        var result = await processor.ProcessAsync(databasePath, source, settings, force: false);

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize, result.Message);
        result.Job.OutputPath.Should().Be(output);
        encoder.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RemoteConnectionFailureLeavesTheJobInterruptibleAndBoundToItsWorkspace()
    {
        var source = Path.Combine(_directory, "remote.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var databasePath = Path.Combine(_directory, "remote.db");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var processor = new JobProcessor(
            repository,
            new ReadableFiles(),
            _ => new H264Probe(),
            new FixedSessionFactory(new UnavailableRemoteSession()),
            new FileFingerprintService(),
            new OutputManifestStore(),
            new PassingValidator());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = databasePath },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Eligibility = Rules(),
            Processing = new ProcessingSettings { Mode = "remoteSsh", RemoteSsh = new RemoteSshSettings { Host = "video-worker", WorkingDirectory = "/var/tmp/video-optimiser" } }
        };
        var reported = new List<JobProgress>();

        var result = await processor.ProcessAsync(databasePath, source, settings, force: false, new SynchronousProgress(reported.Add));

        Steps(reported).Should().Equal("Worker unreachable: Remote unavailable. Run the same command again to resume.");
        reported.Single().Tone.Should().Be(JobTone.Failure);
        result.Job.Status.Should().Be(JobStatus.Interrupted);
        result.Job.ResumeStatus.Should().Be(JobStatus.Staging);
        result.Job.ExecutionMode.Should().Be("remoteSsh");
        result.Job.RemoteHost.Should().Be("video-worker");
        result.Job.RemoteWorkspace.Should().Be($"/var/tmp/video-optimiser/{result.Job.Id:N}");
    }

    [Theory]
    [InlineData(CancellationStage.Encoding, JobStatus.Encoding)]
    [InlineData(CancellationStage.Downloading, JobStatus.Downloading)]
    [InlineData(CancellationStage.Validating, JobStatus.Validating)]
    public async Task CtrlCOnARemoteJobKeepsTheStageItStoppedInForTheRerun(CancellationStage cancellationStage, JobStatus expectedResumeStatus)
    {
        var source = Path.Combine(_directory, $"cancel-{cancellationStage}.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var databasePath = Path.Combine(_directory, $"cancel-{cancellationStage}.db");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var session = new CancellingRemoteSession(cancellationStage);
        var processor = new JobProcessor(
            repository,
            new ReadableFiles(),
            _ => new H264Probe(),
            new FixedSessionFactory(session),
            new FileFingerprintService(),
            new OutputManifestStore(),
            cancellationStage == CancellationStage.Validating ? new CancellingValidator() : new PassingValidator());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = databasePath },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Eligibility = Rules(),
            Processing = new ProcessingSettings { Mode = "remoteSsh", RemoteSsh = new RemoteSshSettings { Host = "video-worker", WorkingDirectory = "/var/tmp/video-optimiser" } }
        };

        var action = async () => await processor.ProcessAsync(databasePath, source, settings, force: false);

        var interrupted = (await action.Should().ThrowAsync<JobInterruptedException>()).Which;
        interrupted.Stage.Should().Be(expectedResumeStatus);
        interrupted.Remote.Should().BeTrue();
        var job = (await repository.ListAsync(databasePath, terminal: false)).Should().ContainSingle().Subject;
        job.Status.Should().Be(JobStatus.Interrupted);
        job.ResumeStatus.Should().Be(expectedResumeStatus);
    }

    [Fact]
    public async Task RerunningAnInterruptedRemoteEncodeReattachesToTheSameAttempt()
    {
        var source = Path.Combine(_directory, "reattach.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var databasePath = Path.Combine(_directory, "reattach.db");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = databasePath },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Eligibility = Rules(),
            Processing = new ProcessingSettings { Mode = "remoteSsh", RemoteSsh = new RemoteSshSettings { Host = "video-worker", WorkingDirectory = "/var/tmp/video-optimiser" } }
        };
        JobProcessor Processor(IProcessingSession session) => new(repository, new ReadableFiles(), _ => new H264Probe(), new FixedSessionFactory(session), new FileFingerprintService(), new OutputManifestStore(), new PassingValidator());

        var stopped = async () => await Processor(new CancellingRemoteSession(CancellationStage.Encoding)).ProcessAsync(databasePath, source, settings, force: false);
        await stopped.Should().ThrowAsync<JobInterruptedException>();
        var rerun = new CancellingRemoteSession(CancellationStage.None);
        var result = await Processor(rerun).ProcessAsync(databasePath, source, settings, force: false);

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize);
        rerun.EncodeAttempts.Should().Equal(1);
        result.Job.Attempt.Should().Be(1);
    }

    [Theory]
    [InlineData(JobStatus.Staging, JobStatus.Encoding, JobStatus.Encoding)]
    [InlineData(JobStatus.Queued, null, JobStatus.Queued)]
    [InlineData(JobStatus.Downloading, JobStatus.Encoding, JobStatus.Downloading)]
    public void AJobStoppedBeforeReachingAStageOfItsOwnKeepsItsEarlierResumePoint(JobStatus current, JobStatus? earlier, JobStatus expected)
    {
        JobProcessor.ResumePoint(current, earlier).Should().Be(expected);
    }

    [Fact]
    public async Task AJobWhoseCrfSearchFindsNothingWorthwhileEndsSkippedNotFailedAndReportsIt()
    {
        var source = Path.Combine(_directory, "clip.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "skipped.db") },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
            Eligibility = Rules(),
            Quality = new QualitySettings { MinimumVmaf = 97, MaxEncodedPercent = 80 }
        };
        var search = new ThrowingCrfSearch(new NoWorthwhileCrfException([new CrfAttempt(31, 97.31, 95)], settings.Quality));
        var reported = new List<JobProgress>();
        var processor = new JobProcessor(repository, new ReadableFiles(), _ => new H264Probe(), new ProcessingSessionFactory(_ => search, _ => new FakeEncoder(), new UnexpectedExternalProcessRunner()), new FileFingerprintService(), new OutputManifestStore(), new PassingValidator());

        var result = await processor.ProcessAsync(settings.Database.Path, source, settings, force: false, new SynchronousProgress(reported.Add));

        result.ExitCode.Should().Be(ExitCode.Success);
        var stored = (await repository.GetAsync(settings.Database.Path, result.Job.Id))!;
        stored.Status.Should().Be(JobStatus.Skipped);
        stored.FailureCategory.Should().Be("NotWorthConverting");
        stored.FailureMessage.Should().Be("AV1 would make it 95% of the original's size at the best quality-passing setting (CRF 31, VMAF 97.3); the limit is 80% (quality.maxEncodedPercent).");
        stored.QualityFingerprint.Should().Be(QualityFingerprint.Compute(settings.Quality));
        stored.CompletedUtc.Should().NotBeNull();
        // The header names the file, so the outcome line does not repeat it, and nothing follows the outcome line.
        Steps(reported).Should().Equal(
            "Finding the best CRF for VMAF 97 (2 test clips of 20s each)...",
            $"Not worth converting: {stored.FailureMessage}");
        reported.Last().Tone.Should().Be(JobTone.Warning);
    }

    [Fact]
    public async Task ASuccessfulLocalJobReportsEachStepAndEndsWithItsOutcome()
    {
        var source = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var settings = Settings("steps.db", new QualitySettings { MinimumVmaf = 97 });
        var processor = Processor(new PredictedCrfSearch(), new FakeEncoder());
        var reported = new List<JobProgress>();

        var result = await processor.ProcessAsync(settings.Database.Path, source, settings, force: false, new SynchronousProgress(reported.Add));

        result.Job.Status.Should().Be(JobStatus.ReadyToFinalize);
        Steps(reported).Should().Equal(
            "Finding the best CRF for VMAF 97 (2 test clips of 20s each)...",
            "Chose CRF 29 in 7m 36s. Expected output: 284.9 MiB (29% of the original).",
            "Encoding at CRF 29, expected to take about 5 minutes...",
            "Checking the output: streams, duration, savings and decode test...",
            "Ready to finalise: 100 B -> 50 B, 50.0% saved.");
        // No staging, hashing or download lines for a local job, and no job header: the caller owns that.
        reported.Should().NotContain(line => line.Kind == JobProgressKind.Header);
    }

    [Fact]
    public async Task TheCrfSummaryDropsTheEstimateWhenAbAv1PrintedNone()
    {
        var source = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var settings = Settings("noestimate.db", new QualitySettings { MinimumVmaf = 95.5 });
        var reported = new List<JobProgress>();

        _ = await Processor(new FixedCrfSearch(), new FakeEncoder()).ProcessAsync(settings.Database.Path, source, settings, force: false, new SynchronousProgress(reported.Add));

        Steps(reported).Take(3).Should().Equal(
            "Finding the best CRF for VMAF 95.5 (2 test clips of 20s each)...",
            "Chose CRF 42 in 0s.",
            "Encoding at CRF 42...");
    }

    [Fact]
    public async Task TheValidationLineOmitsTheDecodeTestWhenItIsOff()
    {
        var source = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var settings = Settings("nodecode.db");
        settings.Validation.DecodeTest = DecodeTestModes.None;
        var reported = new List<JobProgress>();

        _ = await Processor(new FixedCrfSearch(), new FakeEncoder()).ProcessAsync(settings.Database.Path, source, settings, force: false, new SynchronousProgress(reported.Add));

        Steps(reported).Should().Contain("Checking the output: streams, duration, savings...");
    }

    [Fact]
    public async Task AbAv1LogLinesReachTheConsoleTranslatedByAFreshTranslatorPerStage()
    {
        var source = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var settings = Settings("translated.db", new QualitySettings { MinimumVmaf = 97 });
        var reported = new List<JobProgress>();

        _ = await Processor(new LoggingCrfSearch(), new LoggingEncoder()).ProcessAsync(settings.Database.Path, source, settings, force: false, new SynchronousProgress(reported.Add));

        reported.Should().NotContain(line => line.Kind == JobProgressKind.ToolOutput);
        reported.Should().Contain(JobProgress.Detail("CRF 34   VMAF 95.02   size  19%   below target"));
        reported.Should().Contain(line => line.Kind == JobProgressKind.Step && line.Text.StartsWith("Encoded 99.8 MiB (29% of the original) in ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACrfSearchFailureKeepsTheFullLogAndNamesItInTheOutcomeAndTheStoredFailure()
    {
        var source = Path.Combine(_directory, "broken.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var settings = Settings("crflog.db");
        var reported = new List<JobProgress>();

        var result = await Processor(new FailingLoggingCrfSearch(), new FakeEncoder()).ProcessAsync(settings.Database.Path, source, settings, force: false, new SynchronousProgress(reported.Add));

        var log = Path.Combine(_directory, ".video-optimiser", $"{result.Job.Id:N}.crf.log");
        result.ExitCode.Should().Be(ExitCode.ProcessingFailure);
        result.Job.Status.Should().Be(JobStatus.Failed);
        result.Job.QualityFingerprint.Should().BeNull("a failed search judged nothing about the quality bar");
        File.ReadAllLines(log).Should().Equal("first ab-av1 line", "Error: ffmpeg exploded");
        Steps(reported).Should().Equal(
            "Finding the best CRF for VMAF 95 (2 test clips of 20s each)...",
            "Failed during the CRF search: ab-av1 crf-search failed with exit code 1: ffmpeg exploded",
            $"Full log: {log}");
        result.Job.FailureCategory.Should().Be("CrfSearchFailed");
        result.Job.FailureMessage.Should().Be($"ab-av1 crf-search failed with exit code 1: ffmpeg exploded Full log: {log}");
        TemporaryOutputClassifier.TryParse(Path.GetFileName(log), out _, out _).Should().BeFalse("cleanup must never treat a kept log as an abandoned output");
    }

    [Fact]
    public async Task AnEncodeFailureKeepsTheLogOfThatAttempt()
    {
        var source = Path.Combine(_directory, "broken.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var settings = Settings("encodelog.db");
        var reported = new List<JobProgress>();

        var result = await Processor(new FixedCrfSearch(), new FailingLoggingEncoder()).ProcessAsync(settings.Database.Path, source, settings, force: false, new SynchronousProgress(reported.Add));

        var log = Path.Combine(_directory, ".video-optimiser", $"{result.Job.Id:N}.encode-1.log");
        File.ReadAllLines(log).Should().Contain("Error: disk full");
        Steps(reported).TakeLast(2).Should().Equal("Failed during encoding: ab-av1 encode failed with exit code 1.", $"Full log: {log}");
        result.Job.FailureMessage.Should().EndWith($"Full log: {log}");
    }

    [Fact]
    public async Task AValidationFailureIsTheOutcomeWithItsFailuresJoined()
    {
        var source = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var settings = Settings("invalid.db");
        var processor = new JobProcessor(new SqliteJobRepository(new SqliteDatabaseInitializer()), new ReadableFiles(), _ => new H264Probe(), new ProcessingSessionFactory(_ => new FixedCrfSearch(), _ => new FakeEncoder(), new UnexpectedExternalProcessRunner()), new FileFingerprintService(), new OutputManifestStore(), new FailingValidator());
        var reported = new List<JobProgress>();

        var result = await processor.ProcessAsync(settings.Database.Path, source, settings, force: false, new SynchronousProgress(reported.Add));

        result.ExitCode.Should().Be(ExitCode.ValidationFailure);
        Steps(reported).Last().Should().Be("Failed validation: duration differs; no streams");
        reported.Last().Tone.Should().Be(JobTone.Failure);
    }

    private AppSettings Settings(string databaseName, QualitySettings? quality = null) => new()
    {
        Database = new DatabaseSettings { Path = Path.Combine(_directory, databaseName) },
        Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
        Eligibility = Rules(),
        Quality = quality ?? new QualitySettings()
    };

    private static JobProcessor Processor(ICrfSearchClient search, IVideoEncoder encoder) => new(
        new SqliteJobRepository(new SqliteDatabaseInitializer()),
        new ReadableFiles(),
        _ => new H264Probe(),
        new ProcessingSessionFactory(_ => search, _ => encoder, new UnexpectedExternalProcessRunner()),
        new FileFingerprintService(),
        new OutputManifestStore(),
        new PassingValidator());

    private static string[] Steps(IEnumerable<JobProgress> reported) => [.. reported.Where(line => line.Kind == JobProgressKind.Step).Select(line => line.Text)];

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class PredictedCrfSearch : ICrfSearchClient
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) => [];
        public Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CrfSearchResult(29, string.Empty, string.Empty, TimeSpan.FromSeconds(456), new CrfPrediction("284.87 MiB", 29, "5 minutes")));
    }

    private sealed class LoggingCrfSearch : ICrfSearchClient
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) => [];
        public Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(JobProgress.Tool("[2026-10-13T10:10:36Z INFO  ab_av1::command::crf_search] crf 34 VMAF 95.02 (19%)"));
            return Task.FromResult(new CrfSearchResult(29, string.Empty, string.Empty, TimeSpan.Zero));
        }
    }

    private sealed class LoggingEncoder : IVideoEncoder
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams) => [];
        public async Task<EncodeResult> EncodeAsync(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(JobProgress.Tool("Encoded 99.8 MiB (29%)"));
            await File.WriteAllTextAsync(outputPath, "av1", cancellationToken);
            return new EncodeResult(outputPath, TimeSpan.Zero);
        }
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
        public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams) => [];
        public Task<EncodeResult> EncodeAsync(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(JobProgress.Tool("Error: disk full"));
            throw new InvalidOperationException("ab-av1 encode failed with exit code 1.");
        }
    }

    private sealed class FailingValidator : IOutputValidationService
    {
        public Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationReport { Passed = false, Failures = ["duration differs", "no streams"], ValidatedUtc = DateTimeOffset.UtcNow });
    }

    private sealed class SynchronousProgress(Action<JobProgress> report) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => report(value);
    }

    private sealed class ReadableFiles : IFileReadinessService
    {
        public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new FileReadinessResult(true, "Readable."));
    }

    private sealed class H264Probe(IReadOnlyList<string>? audioCodecs = null) : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaInfo(path.EndsWith(".encoding.mp4", StringComparison.OrdinalIgnoreCase) ? "av1" : "h264", 1, 1, 0, 0, 120, new FileInfo(path).Length, 1920, 1080, 10_000_000, audioCodecs));
    }

    private sealed class FixedCrfSearch : ICrfSearchClient
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) => [];
        public Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new CrfSearchResult(42, string.Empty, string.Empty, TimeSpan.Zero));
    }

    private sealed class ThrowingCrfSearch(Exception exception) : ICrfSearchClient
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) => [];
        public Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => throw exception;
    }

    private sealed class CancellingCrfSearch : ICrfSearchClient
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) => [];
        public Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => throw new OperationCanceledException();
    }

    private sealed class FakeEncoder : IVideoEncoder
    {
        public int CallCount { get; private set; }
        public string? OutputPath { get; private set; }
        public IReadOnlyList<int>? FlacAudioStreams { get; private set; }
        public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams) => [];
        public async Task<EncodeResult> EncodeAsync(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            OutputPath = outputPath;
            FlacAudioStreams = flacAudioStreams;
            await File.WriteAllTextAsync(outputPath, "av1", cancellationToken);
            return new EncodeResult(outputPath, TimeSpan.Zero);
        }
    }

    private sealed class PassingValidator : IOutputValidationService
    {
        public Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(new ValidationReport { Passed = true, SourceSizeBytes = 100, OutputSizeBytes = 50, PercentageSaved = 50, ValidatedUtc = DateTimeOffset.UtcNow });
    }

    private sealed class UnexpectedExternalProcessRunner : IExternalProcessRunner
    {
        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("External processes are not expected for local processing tests.");
    }

    private sealed class FixedSessionFactory(IProcessingSession session) : IProcessingSessionFactory
    {
        public IProcessingSession Create(JobRecord job, AppSettings settings) => session;
    }

    private sealed class UnavailableRemoteSession : IProcessingSession
    {
        public bool IsRemote => true;
        public string ExecutionMode => "remoteSsh";
        public string? RemoteHost => "video-worker";
        public string? RemoteWorkspace => null;
        public Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default) => throw new RemoteConnectionException("Remote unavailable.");
        public Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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

        public Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            EncodeAttempts.Add(attempt);
            if (cancellationStage == CancellationStage.Encoding) throw new OperationCanceledException();
            return Task.FromResult(new ProcessingStageResult(outputPath, TimeSpan.Zero));
        }

        public async Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default)
        {
            if (cancellationStage == CancellationStage.Downloading) throw new OperationCanceledException();
            await File.WriteAllTextAsync(outputPath, "av1", cancellationToken);
        }

        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellingValidator : IOutputValidationService
    {
        public Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default) => throw new OperationCanceledException();
    }

    private static EligibilitySettings Rules() => new EligibilitySettings
    {
        Rules = new List<EligibilityRuleSettings> { new() { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "1Mbps", MinimumFileSize = "1B" } }
    };
}
