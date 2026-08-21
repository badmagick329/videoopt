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

    [Fact]
    public void NeverStartedQueuedJobAdoptsTheCurrentManagedRemoteBinding()
    {
        var job = new JobRecord { Id = Guid.NewGuid(), Status = JobStatus.Queued };

        var result = JobCompatibility.Evaluate(job, ManagedSettings());

        result.Status.Should().Be(JobCompatibilityStatus.Adopted);
        JobCompatibility.Apply(job, result.ExpectedBinding);
        job.ExecutionMode.Should().Be(ProcessingModes.RemoteSsh);
        job.RemoteHost.Should().Be("hetzner:cx43:hel1,fsn1");
        job.RemoteWorkspace.Should().Be($"/var/tmp/video-optimiser/{job.Id:N}");
    }

    [Theory]
    [InlineData(JobStatus.Interrupted)]
    [InlineData(JobStatus.Staging)]
    [InlineData(JobStatus.CrfSearching)]
    [InlineData(JobStatus.Encoding)]
    [InlineData(JobStatus.Downloading)]
    [InlineData(JobStatus.Validating)]
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

        var result = await queue.PrepareAsync(settings.Database.Path, settings);

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

        var result = await queue.PrepareAsync(settings.Database.Path, settings);

        result.FailedJobs.Should().BeEmpty();
        result.RunnableJobs.Should().ContainSingle().Which.RemoteHost.Should().Be("hetzner:cx43:hel1,fsn1");
        (await repository.GetAsync(settings.Database.Path, job.Id))!.RemoteWorkspace.Should().Be($"/var/tmp/video-optimiser/{job.Id:N}");
    }

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

        var result = await queue.PrepareAsync(settings.Database.Path, settings);

        result.RunnableJobs.Should().BeEmpty();
        result.FailedJobs.Should().ContainSingle().Which.FailureCategory.Should().Be("ManualInterventionRequired");
        var stored = (await repository.GetAsync(settings.Database.Path, id))!;
        stored.Status.Should().Be(JobStatus.Failed);
        stored.FailureCategory.Should().Be("ManualInterventionRequired");
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

        var result = await retry.RetryRemoteConfigurationChangedAsync(settings.Database.Path, original.Id, settings);

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
        var second = await retry.RetryRemoteConfigurationChangedAsync(settings.Database.Path, original.Id, settings);
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
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Interrupted)]
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

        var result = await retry.RetryRemoteConfigurationChangedAsync(settings.Database.Path, original.Id, settings);

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

        var preparation = await queue.PrepareAsync(settings.Database.Path, settings);
        var run = await queue.RunAsync(settings.Database.Path, settings, preparation.RunnableJobs);

        QueueExitCode.Calculate(run.ReadyToFinalize, preparation.FailedJobs.Count + run.FailedJobs.Count).Should().Be(ExitCode.PartialSuccess);
    }

    [Fact]
    public void DeletionPolicyKeepsWorkersAfterFailureOrInterruption()
    {
        ManagedWorkerDeletionPolicy.Decide(true, true, hasFailures: false, interrupted: false).Should().Be(ManagedWorkerDeletionDecision.DeleteAutomatically);
        ManagedWorkerDeletionPolicy.Decide(true, true, hasFailures: true, interrupted: false).Should().Be(ManagedWorkerDeletionDecision.RetainAfterFailure);
        ManagedWorkerDeletionPolicy.Decide(true, true, hasFailures: true, interrupted: true).Should().Be(ManagedWorkerDeletionDecision.RetainInterrupted);
        ManagedWorkerDeletionPolicy.Decide(false, true, hasFailures: false, interrupted: false).Should().Be(ManagedWorkerDeletionDecision.None);
    }

    [Theory]
    [InlineData("y", true)]
    [InlineData("yes", true)]
    [InlineData("Y", true)]
    [InlineData("n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("delete", false)]
    public void OnlyExplicitAffirmativeConfirmationDeletesAFailedWorker(string? response, bool expected)
    {
        ManagedWorkerDeletionPolicy.IsAffirmative(response).Should().Be(expected);
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
                Hetzner = new HetznerSettings { ServerType = "cx43", Locations = ["hel1", "fsn1"] }
            }
        }
    };

    private sealed class EmptyScanner : IFileScanner
    {
        public Task<ScanReport> ScanAsync(IReadOnlyList<WatchRootSettings> roots, AppSettings settings, bool stopAfterFirstEligible = false, IReadOnlySet<string>? openSourcePaths = null, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new ScanReport([], []));
    }

    private sealed class UnexpectedProcessor : IJobProcessor
    {
        public Task<JobProcessingResult> ProcessAsync(string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Processing is not expected during preparation.");
    }

    private sealed class ReadyProcessor : IJobProcessor
    {
        public Task<JobProcessingResult> ProcessAsync(string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JobProcessingResult(new JobRecord { Id = Guid.NewGuid(), SourcePath = sourcePath, Status = JobStatus.ReadyToFinalize }, ExitCode.Success, "Ready."));
    }
}
