using FluentAssertions;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Jobs;

namespace VideoOptimiser.UnitTests;

public sealed class SqliteJobRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    public SqliteJobRepositoryTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "jobs.db");
    }

    [Fact]
    public async Task FindsOnlyTheActiveJobForTheSameSourceAndFingerprint()
    {
        var active = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Encoding));
        var completed = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Completed));

        var found = await _repository.FindActiveAsync(_databasePath, active.SourcePath.ToUpperInvariant(), active.SourceFingerprint);

        found!.Id.Should().Be(active.Id);
        (await _repository.ListAsync(_databasePath, terminal: true)).Should().ContainSingle(job => job.Id == completed.Id);
    }

    [Fact]
    public async Task MarksOnlyInFlightJobsInterrupted()
    {
        var encoding = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Encoding));
        var ready = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.ReadyToFinalize));

        await _repository.MarkActiveJobsInterruptedAsync(_databasePath);

        (await _repository.GetAsync(_databasePath, encoding.Id))!.Status.Should().Be(JobStatus.Interrupted);
        (await _repository.GetAsync(_databasePath, ready.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    [Fact]
    public async Task RejectsInvalidStateTransitions()
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        job.Status = JobStatus.Completed;

        var action = async () => await _repository.UpdateAsync(_databasePath, job);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task PersistsRemoteExecutionIdentity()
    {
        var job = NewJob(JobStatus.Queued);
        job.ExecutionMode = "remoteSsh";
        job.RemoteHost = "video-worker";
        job.RemoteWorkspace = $"/var/tmp/video-optimiser/{job.Id:N}";

        await _repository.CreateAsync(_databasePath, job);

        var stored = await _repository.GetAsync(_databasePath, job.Id);
        stored!.ExecutionMode.Should().Be("remoteSsh");
        stored.RemoteHost.Should().Be("video-worker");
        stored.RemoteWorkspace.Should().Be(job.RemoteWorkspace);
    }

    [Fact]
    public async Task PersistsImmichIdentity()
    {
        var job = await _repository.CreateAsync(_databasePath, NewImmichJob("asset-1", JobStatus.Queued));

        var stored = await _repository.GetAsync(_databasePath, job.Id);
        stored!.SourceKind.Should().Be(JobSourceKind.Immich);
        stored.ImmichAssetId.Should().Be("asset-1");
        stored.ImmichChecksum.Should().Be("q0zr+Ia/8Vh9EfuB4P5uB1j7zN0=");
        stored.ImmichNewAssetId.Should().BeNull();

        stored.Status = JobStatus.Staging;
        stored.ImmichNewAssetId = "asset-new";
        await _repository.UpdateAsync(_databasePath, stored);

        var updated = await _repository.GetAsync(_databasePath, job.Id);
        updated!.ImmichNewAssetId.Should().Be("asset-new");
        updated.SourceKind.Should().Be(JobSourceKind.Immich);
    }

    [Fact]
    public async Task FileJobsDefaultToFileSourceKind()
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));

        var stored = await _repository.GetAsync(_databasePath, job.Id);
        stored!.SourceKind.Should().Be(JobSourceKind.File);
        stored.ImmichAssetId.Should().BeNull();
    }

    [Fact]
    public async Task FindsTheOpenJobForAnImmichAssetOnly()
    {
        await _repository.CreateAsync(_databasePath, NewImmichJob("asset-1", JobStatus.Completed));
        await _repository.CreateAsync(_databasePath, NewImmichJob("asset-1", JobStatus.Failed));
        await _repository.CreateAsync(_databasePath, NewImmichJob("asset-2", JobStatus.Encoding));

        (await _repository.FindOpenByImmichAssetAsync(_databasePath, "asset-1")).Should().BeNull();

        var open = await _repository.CreateAsync(_databasePath, NewImmichJob("asset-1", JobStatus.ReadyToFinalize));
        (await _repository.FindOpenByImmichAssetAsync(_databasePath, "asset-1"))!.Id.Should().Be(open.Id);
    }

    [Fact]
    public async Task ListsOnlyCompletedImmichJobsOldestFirst()
    {
        var newer = NewImmichJob("asset-newer", JobStatus.Completed);
        newer.CompletedUtc = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        var older = NewImmichJob("asset-older", JobStatus.Completed);
        older.CompletedUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await _repository.CreateAsync(_databasePath, newer);
        await _repository.CreateAsync(_databasePath, older);
        await _repository.CreateAsync(_databasePath, NewImmichJob("asset-failed", JobStatus.Failed));
        await _repository.CreateAsync(_databasePath, NewImmichJob("asset-open", JobStatus.ReadyToFinalize));
        await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Completed));

        var completed = await _repository.ListCompletedImmichAsync(_databasePath);

        completed.Select(job => job.ImmichAssetId).Should().Equal("asset-older", "asset-newer");
    }

    [Fact]
    public async Task StoresAndReplacesImmichSkips()
    {
        var recorded = new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-1", "checksum-a", "rules-a", "codec hevc matches no rule", recorded));
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-2", "checksum-b", "rules-a", "shorter than 10 s", recorded));
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-1", "checksum-c", "rules-b", "bitrate too low", recorded.AddDays(1)));

        var skips = await _repository.ListImmichSkipsAsync(_databasePath);

        skips.Should().HaveCount(2);
        skips["asset-1"].Should().Be(new ImmichSkipRecord("asset-1", "checksum-c", "rules-b", "bitrate too low", recorded.AddDays(1)));
        skips["asset-2"].Reason.Should().Be("shorter than 10 s");
    }

    [Theory]
    [InlineData("checksum-a", "rules-a", true)]
    [InlineData("checksum-b", "rules-a", false)]
    [InlineData("checksum-a", "rules-b", false)]
    public void ASkipGoesStaleWhenTheFileOrTheRulesChange(string checksum, string rulesFingerprint, bool current)
    {
        var skip = new ImmichSkipRecord("asset-1", "checksum-a", "rules-a", "reason", DateTimeOffset.UtcNow);

        skip.IsCurrent(checksum, rulesFingerprint).Should().Be(current);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static JobRecord NewJob(JobStatus status) => new()
    {
        Id = Guid.NewGuid(),
        SourcePath = "C:\\Videos\\movie.mp4",
        SourceFingerprint = "fingerprint",
        Status = status
    };

    private static JobRecord NewImmichJob(string assetId, JobStatus status) => new()
    {
        Id = Guid.NewGuid(),
        SourcePath = $@"C:\Staging\{assetId}.mp4",
        SourceFingerprint = "fingerprint",
        Status = status,
        SourceKind = JobSourceKind.Immich,
        ImmichAssetId = assetId,
        ImmichChecksum = "q0zr+Ia/8Vh9EfuB4P5uB1j7zN0="
    };
}
