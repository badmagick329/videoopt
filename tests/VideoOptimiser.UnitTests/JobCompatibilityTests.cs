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

public sealed class JobCompatibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public JobCompatibilityTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("hetzner:cx43:hel1,fsn1,nbg1")]
    [InlineData(RemoteExecutionIdentity.ManagedHetzner)]
    public void StartedJobKeepsResumingAfterTheManagedServerTypeOrLocationsChange(string storedBinding)
    {
        var id = Guid.NewGuid();
        var job = new JobRecord
        {
            Id = id,
            Status = JobStatus.Interrupted,
            ResumeStatus = JobStatus.Encoding,
            Crf = 30,
            Attempt = 1,
            ExecutionMode = ProcessingModes.RemoteSsh,
            RemoteHost = storedBinding,
            RemoteWorkspace = $"/var/tmp/video-optimiser/{id:N}"
        };

        JobCompatibility.Evaluate(job, ManagedSettings()).Status.Should().Be(JobCompatibilityStatus.Compatible);
    }

    [Fact]
    public void ManagedHetznerJobDoesNotMatchAManualAliasNamedHetzner()
    {
        var id = Guid.NewGuid();
        var settings = ManagedSettings();
        settings.Processing.RemoteSsh.Lifecycle = RemoteLifecycleModes.Manual;
        settings.Processing.RemoteSsh.Host = "hetzner";
        var job = new JobRecord
        {
            Id = id,
            Status = JobStatus.Interrupted,
            ResumeStatus = JobStatus.Encoding,
            Crf = 30,
            Attempt = 1,
            ExecutionMode = ProcessingModes.RemoteSsh,
            RemoteHost = RemoteExecutionIdentity.ManagedHetzner,
            RemoteWorkspace = $"/var/tmp/video-optimiser/{id:N}"
        };

        JobCompatibility.Evaluate(job, settings).Status.Should().Be(JobCompatibilityStatus.Incompatible);
    }

    [Theory]
    [InlineData(JobStatus.Interrupted)]
    public void StartedJobRefusesAChangedRemoteBinding(JobStatus status)
    {
        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            Status = status,
            ResumeStatus = status == JobStatus.Interrupted ? JobStatus.Encoding : null,
            ExecutionMode = ProcessingModes.RemoteSsh,
            RemoteHost = "hetzner:cx43:nbg1",
            RemoteWorkspace = "/var/tmp/video-optimiser/old"
        };

        var result = JobCompatibility.Evaluate(job, ManagedSettings());

        result.Status.Should().Be(JobCompatibilityStatus.Incompatible);
        result.Message.Should().Be(JobCompatibility.RemoteConfigurationChangedMessage);
    }

    [Fact]
    public async Task PreparationFailsAnIncompatibleJobBeforeAnyJobCanRun()
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = ManagedSettings();
        settings.Database.Path = Path.Combine(_directory, "jobs.db");
        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = Path.Combine(_directory, "movie.mp4"),
            SourceFingerprint = "source",
            Status = JobStatus.Interrupted,
            ResumeStatus = JobStatus.Encoding,
            Crf = 30,
            Attempt = 1,
            ExecutionMode = ProcessingModes.RemoteSsh,
            RemoteHost = "hetzner:cx43:nbg1",
            RemoteWorkspace = "/var/tmp/video-optimiser/old"
        };
        await repository.CreateAsync(settings.Database.Path, job);
        var queue = new QueueService(new EmptyScanner(), repository, new FileFingerprintService(), new UnexpectedProcessor());

        var result = await queue.PrepareAsync(settings.Database.Path, settings, JobSourceKind.File);

        result.RunnableJobs.Should().BeEmpty();
        result.FailedJobs.Should().ContainSingle().Which.FailureCategory.Should().Be(JobCompatibility.RemoteConfigurationChangedCategory);
        (await repository.GetAsync(settings.Database.Path, job.Id)).Should().Match<JobRecord>(stored =>
            stored.Status == JobStatus.Failed && stored.FailureCategory == JobCompatibility.RemoteConfigurationChangedCategory);
    }

    [Fact]
    public async Task PreparationAdoptsAQueuedJobAndMakesItRunnable()
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = ManagedSettings();
        settings.Database.Path = Path.Combine(_directory, "jobs.db");
        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = Path.Combine(_directory, "movie.mp4"),
            SourceFingerprint = "source",
            Status = JobStatus.Queued
        };
        await repository.CreateAsync(settings.Database.Path, job);
        var queue = new QueueService(new EmptyScanner(), repository, new FileFingerprintService(), new UnexpectedProcessor());

        var result = await queue.PrepareAsync(settings.Database.Path, settings, JobSourceKind.File);

        result.FailedJobs.Should().BeEmpty();
        result.RunnableJobs.Should().ContainSingle().Which.RemoteHost.Should().Be(RemoteExecutionIdentity.ManagedHetzner);
        (await repository.GetAsync(settings.Database.Path, job.Id))!.RemoteWorkspace.Should().Be($"/var/tmp/video-optimiser/{job.Id:N}");
    }

    [Fact]
    public async Task LocalOverrideSkipsAStartedRemoteJobAndTheNextRunWithoutItResumesIt()
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = ManagedSettings();
        settings.Database.Path = Path.Combine(_directory, "jobs.db");
        var id = Guid.NewGuid();
        var job = new JobRecord
        {
            Id = id,
            SourcePath = Path.Combine(_directory, "movie.mp4"),
            SourceFingerprint = "source",
            Status = JobStatus.Interrupted,
            ResumeStatus = JobStatus.Encoding,
            Crf = 30,
            Attempt = 1,
            ExecutionMode = ProcessingModes.RemoteSsh,
            RemoteHost = RemoteExecutionIdentity.ManagedHetzner,
            RemoteWorkspace = $"/var/tmp/video-optimiser/{id:N}"
        };
        await repository.CreateAsync(settings.Database.Path, job);
        var queue = new QueueService(new EmptyScanner(), repository, new FileFingerprintService(), new UnexpectedProcessor());
        var localRun = ManagedSettings();
        localRun.Database.Path = settings.Database.Path;
        ProcessingModeOverride.Local.Apply(localRun);

        var skipped = await queue.PrepareAsync(settings.Database.Path, localRun, JobSourceKind.File, modeOverridden: true);

        skipped.RunnableJobs.Should().BeEmpty();
        skipped.FailedJobs.Should().BeEmpty();
        JobCompatibility.SkippedMessage(skipped.SkippedJobs.Should().ContainSingle().Subject)
            .Should().Be("Skipped movie.mp4: started on a managed Hetzner server; run without --local/--remote to resume it.");
        (await repository.GetAsync(settings.Database.Path, id)).Should().Match<JobRecord>(stored =>
            stored.Status == JobStatus.Interrupted && stored.ExecutionMode == ProcessingModes.RemoteSsh && stored.FailureCategory == null);

        var resumed = await queue.PrepareAsync(settings.Database.Path, settings, JobSourceKind.File);

        resumed.RunnableJobs.Should().ContainSingle().Which.Id.Should().Be(id);
        resumed.SkippedJobs.Should().BeEmpty();
    }

    [Fact]
    public async Task OverrideStillFailsAStartedJobWhoseRemoteHostChanged()
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = ManagedSettings();
        settings.Database.Path = Path.Combine(_directory, "jobs.db");
        ProcessingModeOverride.Remote.Apply(settings);
        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = Path.Combine(_directory, "movie.mp4"),
            SourceFingerprint = "source",
            Status = JobStatus.Interrupted,
            ResumeStatus = JobStatus.Encoding,
            Crf = 30,
            Attempt = 1,
            ExecutionMode = ProcessingModes.RemoteSsh,
            RemoteHost = "old-box",
            RemoteWorkspace = "/var/tmp/video-optimiser/old"
        };
        await repository.CreateAsync(settings.Database.Path, job);
        var queue = new QueueService(new EmptyScanner(), repository, new FileFingerprintService(), new UnexpectedProcessor());

        var result = await queue.PrepareAsync(settings.Database.Path, settings, JobSourceKind.File, modeOverridden: true);

        result.SkippedJobs.Should().BeEmpty();
        result.FailedJobs.Should().ContainSingle().Which.FailureCategory.Should().Be(JobCompatibility.RemoteConfigurationChangedCategory);
    }

    [Theory]
    [InlineData("--local", ProcessingModes.RemoteSsh)]
    [InlineData("--remote", ProcessingModes.Local)]
    public async Task OverrideAdoptsANeverStartedJobIntoItsMode(string flag, string boundMode)
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var modeOverride = flag == "--local" ? ProcessingModeOverride.Local : ProcessingModeOverride.Remote;
        var settings = ManagedSettings();
        settings.Database.Path = Path.Combine(_directory, "jobs.db");
        modeOverride.Apply(settings);
        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = Path.Combine(_directory, "movie.mp4"),
            SourceFingerprint = "source",
            Status = JobStatus.Queued,
            ExecutionMode = boundMode,
            RemoteHost = boundMode == ProcessingModes.RemoteSsh ? RemoteExecutionIdentity.ManagedHetzner : null
        };
        await repository.CreateAsync(settings.Database.Path, job);
        var queue = new QueueService(new EmptyScanner(), repository, new FileFingerprintService(), new UnexpectedProcessor());

        var result = await queue.PrepareAsync(settings.Database.Path, settings, JobSourceKind.File, modeOverridden: true);

        result.SkippedJobs.Should().BeEmpty();
        result.RunnableJobs.Should().ContainSingle().Which.ExecutionMode.Should().Be(modeOverride.Mode);
        (await repository.GetAsync(settings.Database.Path, job.Id))!.ExecutionMode.Should().Be(modeOverride.Mode);
    }

    [Theory]
    [InlineData(ProcessingModes.Local, null, "this PC")]
    [InlineData(ProcessingModes.RemoteSsh, "encoder", "encoder over SSH")]
    public void SkippedMessageNamesWhereTheJobStarted(string mode, string? host, string binding) =>
        JobCompatibility.SkippedMessage(new JobRecord { SourcePath = Path.Combine(_directory, "clip.mov"), ExecutionMode = mode, RemoteHost = host })
            .Should().Be($"Skipped clip.mov: started on {binding}; run without --local/--remote to resume it.");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreparationProtectsInterruptedFinalizationRegardlessOfRemoteBinding(bool bindingMatches)
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = ManagedSettings();
        settings.Database.Path = Path.Combine(_directory, $"finalization-{bindingMatches}.db");
        var id = Guid.NewGuid();
        var expected = JobCompatibility.ExpectedBinding(id, settings);
        var job = new JobRecord
        {
            Id = id,
            SourcePath = Path.Combine(_directory, "movie.mp4"),
            SourceFingerprint = "source",
            Status = JobStatus.Finalizing,
            ExecutionMode = ProcessingModes.RemoteSsh,
            RemoteHost = bindingMatches ? expected.RemoteIdentity : "hetzner:cx43:nbg1",
            RemoteWorkspace = bindingMatches ? expected.RemoteWorkspace : "/var/tmp/video-optimiser/old"
        };
        await repository.CreateAsync(settings.Database.Path, job);
        var queue = new QueueService(new EmptyScanner(), repository, new FileFingerprintService(), new UnexpectedProcessor());

        var result = await queue.PrepareAsync(settings.Database.Path, settings, JobSourceKind.File);

        // Neither run nor failed: finalize checks whether anything moved and resumes it (VOPT-70).
        result.RunnableJobs.Should().BeEmpty();
        result.FailedJobs.Should().BeEmpty();
        result.InterruptedFinalizations.Should().ContainSingle().Which.Id.Should().Be(id);
        var stored = (await repository.GetAsync(settings.Database.Path, id))!;
        stored.Status.Should().Be(JobStatus.Interrupted);
        stored.ResumeStatus.Should().Be(JobStatus.Finalizing);
    }

    [Fact]
    public async Task RetryCreatesANewQueuedJobAndKeepsTheOriginalFailureInHistory()
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = ManagedSettings();
        settings.Database.Path = Path.Combine(_directory, "retry.db");
        var original = new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = Path.Combine(_directory, "movie.mp4"),
            SourceFingerprint = "old-fingerprint",
            Status = JobStatus.Failed,
            Crf = 32,
            Attempt = 2,
            OutputPath = "old-output.mp4",
            ManifestPath = "old-output.manifest.json",
            FailureCategory = JobCompatibility.RemoteConfigurationChangedCategory,
            FailureMessage = JobCompatibility.RemoteConfigurationChangedMessage,
            CompletedUtc = DateTimeOffset.UtcNow,
            ExecutionMode = ProcessingModes.RemoteSsh,
            RemoteHost = "hetzner:cx43:nbg1",
            RemoteWorkspace = "/var/tmp/video-optimiser/old"
        };
        await repository.CreateAsync(settings.Database.Path, original);
        var retry = new JobRetryService(repository);

        var result = await retry.RetryAsync(settings.Database.Path, original.Id, settings);

        result.Succeeded.Should().BeTrue();
        var replacement = result.Replacement!;
        replacement.Id.Should().NotBe(original.Id);
        replacement.SourcePath.Should().Be(original.SourcePath);
        replacement.RemoteWorkspace.Should().NotBe(original.RemoteWorkspace);
        replacement.Status.Should().Be(JobStatus.Queued);
        replacement.ResumeStatus.Should().BeNull();
        replacement.Crf.Should().BeNull();
        replacement.Attempt.Should().Be(0);
        replacement.OutputPath.Should().BeNull();
        replacement.ManifestPath.Should().BeNull();
        replacement.ValidationPassed.Should().BeNull();
        replacement.FailureCategory.Should().BeNull();
        replacement.FailureMessage.Should().BeNull();
        replacement.CompletedUtc.Should().BeNull();
        var second = await retry.RetryAsync(settings.Database.Path, original.Id, settings);
        second.Succeeded.Should().BeFalse();
        second.Error.Should().Contain(replacement.Id.ToString("N")).And.Contain(JobStatus.Queued.ToString());
        var storedOriginal = (await repository.GetAsync(settings.Database.Path, original.Id))!;
        storedOriginal.Status.Should().Be(JobStatus.Failed);
        storedOriginal.FailureCategory.Should().Be(JobCompatibility.RemoteConfigurationChangedCategory);
        storedOriginal.OutputPath.Should().Be("old-output.mp4");
        (await repository.ListAsync(settings.Database.Path, terminal: true)).Should().ContainSingle(job => job.Id == original.Id);
        (await repository.ListAsync(settings.Database.Path, terminal: false)).Should().ContainSingle(job => job.Id == replacement.Id);
    }

    [Theory]
    [InlineData(JobStatus.Cancelled, "Cancelled", true)]
    [InlineData(JobStatus.Skipped, "NotWorthConverting", true)]
    [InlineData(JobStatus.Completed, null, false)]
    public async Task RetryRequeuesAnyFailedCancelledOrSkippedJobWithItsFingerprint(JobStatus status, string? category, bool retried)
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = ManagedSettings();
        settings.Database.Path = Path.Combine(_directory, $"retry-{status}-{category}.db");
        var original = new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "movie.mp4"), SourceFingerprint = "source", Status = status, FailureCategory = category, CompletedUtc = DateTimeOffset.UtcNow };
        await repository.CreateAsync(settings.Database.Path, original);

        var result = await new JobRetryService(repository).RetryAsync(settings.Database.Path, original.Id, settings);

        result.Succeeded.Should().Be(retried);
        if (retried) result.Replacement!.SourceFingerprint.Should().Be("source");
        else result.Error.Should().Be("Only failed, cancelled or skipped jobs can be retried.");
    }

    [Fact]
    public async Task RetryKeepsAnImmichJobsAssetIdentity()
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = ManagedSettings();
        settings.Database.Path = Path.Combine(_directory, "retry-immich.db");
        var original = new JobRecord
        {
            Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "staging", "asset-1", "PXL.mp4"), SourceFingerprint = "source",
            Status = JobStatus.Failed, FailureCategory = "EncodeFailed", CompletedUtc = DateTimeOffset.UtcNow,
            SourceKind = JobSourceKind.Immich, ImmichAssetId = "asset-1", ImmichChecksum = "checksum"
        };
        await repository.CreateAsync(settings.Database.Path, original);

        var result = await new JobRetryService(repository).RetryAsync(settings.Database.Path, original.Id, settings);

        var stored = await repository.GetAsync(settings.Database.Path, result.Replacement!.Id);
        stored!.SourceKind.Should().Be(JobSourceKind.Immich);
        stored.ImmichAssetId.Should().Be("asset-1");
        stored.ImmichChecksum.Should().Be("checksum");
    }

    /// <summary>R6: a video queued outside the rules once stays outside them, or the retry would only fail it as ineligible.</summary>
    [Fact]
    public async Task RetryKeepsAForcedVideoForced()
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = ManagedSettings();
        settings.Database.Path = Path.Combine(_directory, "retry-forced.db");
        var original = new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "movie.mp4"), SourceFingerprint = "source", Status = JobStatus.Failed, FailureCategory = "EncodeFailed", Forced = true };
        await repository.CreateAsync(settings.Database.Path, original);

        var result = await new JobRetryService(repository).RetryAsync(settings.Database.Path, original.Id, settings);

        (await repository.GetAsync(settings.Database.Path, result.Replacement!.Id))!.Forced.Should().BeTrue();
    }

    /// <summary>R11: a retry that loses the race to another command names the job that command queued.</summary>
    [Fact]
    public async Task RetryThatLosesARaceNamesTheOpenJob()
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = ManagedSettings();
        settings.Database.Path = Path.Combine(_directory, "retry-race.db");
        var original = new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "movie.mp4"), SourceFingerprint = "source", Status = JobStatus.Failed, FailureCategory = "EncodeFailed" };
        await repository.CreateAsync(settings.Database.Path, original);
        var raced = await repository.CreateAsync(settings.Database.Path, new JobRecord { Id = Guid.NewGuid(), SourcePath = original.SourcePath, SourceFingerprint = "source", Status = JobStatus.Queued });

        var result = await new JobRetryService(new FirstLookupMisses(repository)).RetryAsync(settings.Database.Path, original.Id, settings);

        result.Error.Should().Be($"An open job already exists for this original: {raced.Id:N} (Queued).");
    }

    /// <summary>Misses the open job once, as a retry that checked just before another command queued the original would.</summary>
    private sealed class FirstLookupMisses(IJobRepository inner) : DelegatingJobRepository(inner)
    {
        private bool _missed;

        public override Task<JobRecord?> FindOpenBySourceAsync(string databasePath, string sourcePath, CancellationToken cancellationToken = default)
        {
            if (_missed) return Inner.FindOpenBySourceAsync(databasePath, sourcePath, cancellationToken);
            _missed = true;
            return Task.FromResult<JobRecord?>(null);
        }
    }

    [Theory]
    [InlineData(JobStatus.ReadyToFinalize)]
    public async Task RetryIsBlockedByAnyExistingOpenJob(JobStatus openStatus)
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = ManagedSettings();
        settings.Database.Path = Path.Combine(_directory, $"retry-{openStatus}.db");
        var sourcePath = Path.Combine(_directory, "movie.mp4");
        var original = new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = sourcePath,
            SourceFingerprint = "source",
            Status = JobStatus.Failed,
            FailureCategory = JobCompatibility.RemoteConfigurationChangedCategory,
            FailureMessage = JobCompatibility.RemoteConfigurationChangedMessage,
            CompletedUtc = DateTimeOffset.UtcNow
        };
        var openJob = new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = sourcePath,
            SourceFingerprint = "source",
            Status = openStatus
        };
        await repository.CreateAsync(settings.Database.Path, original);
        await repository.CreateAsync(settings.Database.Path, openJob);
        var retry = new JobRetryService(repository);

        var result = await retry.RetryAsync(settings.Database.Path, original.Id, settings);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(openJob.Id.ToString("N")).And.Contain(openStatus.ToString());
        (await repository.GetAsync(settings.Database.Path, original.Id))!.Status.Should().Be(JobStatus.Failed);
        (await repository.ListAsync(settings.Database.Path, terminal: false)).Should().ContainSingle(job => job.Id == openJob.Id);
    }

    [Fact]
    public async Task PreparationFailurePlusReadyJobProducesPartialSuccess()
    {
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var settings = ManagedSettings();
        settings.Database.Path = Path.Combine(_directory, "mixed.db");
        var incompatible = new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = Path.Combine(_directory, "incompatible.mp4"),
            SourceFingerprint = "source",
            Status = JobStatus.Interrupted,
            ResumeStatus = JobStatus.Encoding,
            Crf = 30,
            Attempt = 1,
            ExecutionMode = ProcessingModes.RemoteSsh,
            RemoteHost = "hetzner:cx43:nbg1",
            RemoteWorkspace = "/var/tmp/video-optimiser/old"
        };
        var runnable = new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = Path.Combine(_directory, "runnable.mp4"),
            SourceFingerprint = "source",
            Status = JobStatus.Queued
        };
        await repository.CreateAsync(settings.Database.Path, incompatible);
        await repository.CreateAsync(settings.Database.Path, runnable);
        var queue = new QueueService(new EmptyScanner(), repository, new FileFingerprintService(), new ReadyProcessor());

        var preparation = await queue.PrepareAsync(settings.Database.Path, settings, JobSourceKind.File);
        var run = await queue.RunAsync(settings.Database.Path, settings, preparation);

        QueueExitCode.Calculate(run.ReadyToFinalize, preparation.FailedJobs.Count + run.FailedJobs.Count).Should().Be(ExitCode.PartialSuccess);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static AppSettings ManagedSettings() => new()
    {
        Processing = new ProcessingSettings
        {
            Mode = ProcessingModes.RemoteSsh,
            RemoteSsh = new RemoteSshSettings
            {
                Lifecycle = RemoteLifecycleModes.Hetzner,
                WorkingDirectory = "/var/tmp/video-optimiser",
                Hetzner = new HetznerSettings { ServerTypes = ["cx43"], Locations = ["hel1", "fsn1"] }
            }
        }
    };

    private sealed class EmptyScanner : IFileScanner
    {
        public Task<ScanReport> ScanAsync(IReadOnlyList<WatchRootSettings> roots, AppSettings settings, bool stopAfterFirstEligible = false, IReadOnlySet<string>? openSourcePaths = null, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new ScanReport([], []));
        public Task<FileVerdict> JudgeAsync(string path, AppSettings settings, bool force, CancellationToken cancellationToken = default) => throw new NotSupportedException("Not a queue add <file> test.");
    }

    private sealed class UnexpectedProcessor : IJobProcessor
    {
        public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Processing is not expected during preparation.");
    }

    private sealed class ReadyProcessor : IJobProcessor
    {
        public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(true);

        private readonly HashSet<Guid> _seen = [];

        // It writes nothing, so a run that forgot which videos it tried would loop on the same one forever.
        public Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) =>
            _seen.Add(job.Id)
                ? Task.FromResult(new JobProcessingResult(new JobRecord { Id = Guid.NewGuid(), SourcePath = job.SourcePath, Status = JobStatus.ReadyToFinalize }, ExitCode.Success, "Ready."))
                : throw new InvalidOperationException($"{job.SourcePath} was given to the processor twice.");
    }
}
