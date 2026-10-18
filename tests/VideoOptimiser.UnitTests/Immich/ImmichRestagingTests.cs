using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;

namespace VideoOptimiser.UnitTests.Immich;

/// <summary>
/// What restaging refuses to bring back. The happy path and an asset Immich no longer returns are pinned through the CLI by
/// <c>RunCommandTests</c>; these are the two ways a copy must not be downloaded again, because it would be other bytes or
/// land outside the asset's folder.
/// </summary>
public sealed class ImmichRestagingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly InMemoryImmich _immich = new(new DateTimeOffset(2024, 3, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly AppSettings _settings;

    public ImmichRestagingTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Immich = new ImmichSettings { BaseUrl = "http://immich.test:2283", StagingDirectory = Path.Combine(_directory, "staging") }
        };
    }

    [Fact]
    public async Task AnAssetThatChangedSinceTheJobWasCreatedFailsTheJobAndDeletesTheDownload()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        var job = await JobAsync(asset with { Checksum = InMemoryImmich.Checksum("hevc earlier video") }, Path.Combine(_settings.Immich.StagingDirectory, asset.Id, "clip.mp4"));

        var result = await Restaging().RestageMissingAsync(_settings.Database.Path, _settings, [job]);

        result.Failed.Should().ContainSingle();
        var stored = (await _repository.GetAsync(_settings.Database.Path, job.Id))!;
        (stored.Status, stored.FailureCategory).Should().Be((JobStatus.Failed, "SourceMissing"));
        File.Exists(job.SourcePath).Should().BeFalse();
    }

    [Fact]
    public async Task NeverDownloadsAgainToAPathOutsideTheAssetFolder()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        var job = await JobAsync(asset, Path.Combine(_directory, "elsewhere", "clip.mp4"));

        var result = await Restaging().RestageMissingAsync(_settings.Database.Path, _settings, [job]);

        result.Failed.Should().ContainSingle();
        (await _repository.GetAsync(_settings.Database.Path, job.Id))!.FailureCategory.Should().Be("SourceMissing");
        _immich.Downloads.Should().BeEmpty();
        File.Exists(job.SourcePath).Should().BeFalse();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ImmichRestaging Restaging() => new(_immich, _repository, new FileFingerprintService());

    /// <summary>A queued Immich job whose downloaded copy is missing.</summary>
    private Task<JobRecord> JobAsync(ImmichAsset asset, string path) => _repository.CreateAsync(_settings.Database.Path, new JobRecord
    {
        Id = Guid.NewGuid(),
        SourcePath = path,
        SourceFingerprint = "fingerprint",
        Status = JobStatus.Queued,
        SourceKind = JobSourceKind.Immich,
        ImmichAssetId = asset.Id,
        ImmichChecksum = asset.Checksum
    });
}
