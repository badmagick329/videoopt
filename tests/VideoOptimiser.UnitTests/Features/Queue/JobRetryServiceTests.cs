using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests.Features.Queue;

public sealed class JobRetryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    public JobRetryServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task RetryCreatesANewQueuedJobAndKeepsTheOriginalFailureInHistory()
    {
        var original = new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = SourcePath,
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
        await _repository.CreateAsync(DatabasePath, original);

        var result = await Retry(original);

        result.Succeeded.Should().BeTrue();
        var replacement = result.Replacement!;
        replacement.Id.Should().NotBe(original.Id);
        replacement.RemoteWorkspace.Should().NotBe(original.RemoteWorkspace);
        replacement.Should().BeEquivalentTo(new
        {
            SourcePath,
            Status = JobStatus.Queued,
            ResumeStatus = (JobStatus?)null,
            Crf = (int?)null,
            Attempt = 0,
            OutputPath = (string?)null,
            ManifestPath = (string?)null,
            ValidationPassed = (bool?)null,
            FailureCategory = (string?)null,
            FailureMessage = (string?)null,
            CompletedUtc = (DateTimeOffset?)null
        });
        var storedOriginal = (await _repository.GetAsync(DatabasePath, original.Id))!;
        storedOriginal.Status.Should().Be(JobStatus.Failed);
        storedOriginal.FailureCategory.Should().Be(JobCompatibility.RemoteConfigurationChangedCategory);
        storedOriginal.OutputPath.Should().Be("old-output.mp4");
        (await _repository.ListAsync(DatabasePath, terminal: true)).Should().ContainSingle(job => job.Id == original.Id);
        (await _repository.ListAsync(DatabasePath, terminal: false)).Should().ContainSingle(job => job.Id == replacement.Id);
    }

    /// <summary>
    /// The new job is created beside the original with the same path, so a finished job never blocks a new one for the same
    /// original. The refusal for any other status is pinned by <c>QueueCommandsTests.RetryRefusesAJobItCannotRequeue</c>.
    /// </summary>
    [Theory]
    [InlineData(JobStatus.Cancelled, "Cancelled")]
    [InlineData(JobStatus.Skipped, "NotWorthConverting")]
    [InlineData(JobStatus.Rejected, "Rejected")]
    public async Task RetryRequeuesACancelledSkippedOrRejectedJobWithItsFingerprint(JobStatus status, string category)
    {
        var original = new JobRecord { Id = Guid.NewGuid(), SourcePath = SourcePath, SourceFingerprint = "source", Status = status, FailureCategory = category, CompletedUtc = DateTimeOffset.UtcNow };
        await _repository.CreateAsync(DatabasePath, original);

        var result = await Retry(original);

        result.Succeeded.Should().BeTrue();
        result.Replacement!.SourceFingerprint.Should().Be("source");
    }

    /// <summary>The retry of an Immich video keeps its asset and checksum, and of a forced one (R6) keeps it outside the rules, or the run would fail it as ineligible.</summary>
    [Fact]
    public async Task RetryKeepsAnImmichJobsAssetIdentityAndAForcedVideoForced()
    {
        var original = new JobRecord
        {
            Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "staging", "asset-1", "PXL.mp4"), SourceFingerprint = "source",
            Status = JobStatus.Failed, FailureCategory = "EncodeFailed", CompletedUtc = DateTimeOffset.UtcNow,
            SourceKind = JobSourceKind.Immich, ImmichAssetId = "asset-1", ImmichChecksum = "checksum", Forced = true
        };
        await _repository.CreateAsync(DatabasePath, original);

        var result = await Retry(original);

        var stored = await _repository.GetAsync(DatabasePath, result.Replacement!.Id);
        stored!.SourceKind.Should().Be(JobSourceKind.Immich);
        stored.ImmichAssetId.Should().Be("asset-1");
        stored.ImmichChecksum.Should().Be("checksum");
        stored.Forced.Should().BeTrue();
    }

    /// <summary>R11: a retry that loses the race to another command names the job that command queued.</summary>
    [Fact]
    public async Task RetryThatLosesARaceNamesTheOpenJob()
    {
        var original = new JobRecord { Id = Guid.NewGuid(), SourcePath = SourcePath, SourceFingerprint = "source", Status = JobStatus.Failed, FailureCategory = "EncodeFailed" };
        await _repository.CreateAsync(DatabasePath, original);
        var raced = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = original.SourcePath, SourceFingerprint = "source", Status = JobStatus.Queued });

        var result = await new JobRetryService(new FirstLookupMisses(_repository)).RetryAsync(DatabasePath, original.Id, ManagedWorker.Settings());

        result.Error.Should().Be($"An open job already exists for this original: {raced.Id:N} (Queued).");
    }

    [Fact]
    public async Task RetryIsBlockedByAnyExistingOpenJob()
    {
        var original = new JobRecord { Id = Guid.NewGuid(), SourcePath = SourcePath, SourceFingerprint = "source", Status = JobStatus.Failed, FailureCategory = "EncodeFailed", CompletedUtc = DateTimeOffset.UtcNow };
        var openJob = new JobRecord { Id = Guid.NewGuid(), SourcePath = SourcePath, SourceFingerprint = "source", Status = JobStatus.ReadyToFinalize };
        await _repository.CreateAsync(DatabasePath, original);
        await _repository.CreateAsync(DatabasePath, openJob);

        var result = await Retry(original);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(openJob.Id.ToString("N")).And.Contain(nameof(JobStatus.ReadyToFinalize));
        (await _repository.GetAsync(DatabasePath, original.Id))!.Status.Should().Be(JobStatus.Failed);
        (await _repository.ListAsync(DatabasePath, terminal: false)).Should().ContainSingle(job => job.Id == openJob.Id);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");
    private string SourcePath => Path.Combine(_directory, "movie.mp4");

    private Task<JobRetryResult> Retry(JobRecord original) => new JobRetryService(_repository).RetryAsync(DatabasePath, original.Id, ManagedWorker.Settings());

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
}
