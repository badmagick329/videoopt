using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Immich;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests;

public sealed class ImmichReuploadGuardTests : IDisposable
{
    private static readonly DateTimeOffset Taken = new(2025, 5, 1, 14, 30, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeImmich _immich = new();
    private readonly List<ScanProgress> _progress = [];
    private readonly AppSettings _settings;

    public ImmichReuploadGuardTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Immich = new ImmichSettings { BaseUrl = "http://immich.test:2283", StagingDirectory = Path.Combine(_directory, "staging") }
        };
    }

    [Fact]
    public async Task LeavesAChecksumImmichNoLongerHoldsAlone()
    {
        await ConvertAsync("purged");

        var result = await CheckAsync();

        result.Checked.Should().Be(1);
        result.Trashed.Should().BeEmpty();
        result.Restored.Should().BeEmpty();
        _immich.Trashes.Should().BeEmpty();
    }

    [Fact]
    public async Task LeavesAnOriginalStillInItsTrashWindowAlone()
    {
        var original = _immich.Add("PXL.mp4", "trashed", isTrashed: true);
        await ConvertAsync("trashed", original.Id);

        var result = await CheckAsync();

        result.Trashed.Should().BeEmpty();
        result.Issues.Should().BeEmpty();
        _immich.Trashes.Should().BeEmpty();
    }

    [Fact]
    public async Task TrashesAnOriginalThePhoneUploadedAgainAndReportsHowToFindIt()
    {
        var job = await ConvertAsync("reuploaded");
        var reupload = _immich.Add("PXL_20250501_143000000.mp4", "reuploaded");

        var result = await CheckAsync();

        result.Trashed.Should().ContainSingle().Which.Should().Be(new ImmichReuploadHit(job.Id, reupload.Id, "PXL_20250501_143000000.mp4", new DateTime(2025, 5, 1, 14, 30, 0)));
        _immich.Trashes.Should().ContainSingle().Which.Should().Equal(reupload.Id);
        _immich.Get(reupload.Id).IsTrashed.Should().BeTrue();
    }

    [Fact]
    public async Task NeverTrashesAnOriginalTheUserRestoredFromTheTrash()
    {
        var original = _immich.Add("PXL.mp4", "restored");
        var job = await ConvertAsync("restored", original.Id);

        var result = await CheckAsync();

        result.Restored.Should().ContainSingle().Which.Should().Be(new ImmichReuploadHit(job.Id, original.Id, "PXL.mp4", Taken.DateTime));
        result.Trashed.Should().BeEmpty();
        _immich.Trashes.Should().BeEmpty();
    }

    [Fact]
    public async Task NeverTrashesARestoredOriginalWhoseChecksumAnotherConvertedJobShares()
    {
        var original = _immich.Add("PXL.mp4", "shared");
        await ConvertAsync("shared", original.Id);
        await ConvertAsync("shared");

        var result = await CheckAsync();

        result.Checked.Should().Be(1);
        result.Restored.Should().ContainSingle().Which.AssetId.Should().Be(original.Id);
        _immich.Trashes.Should().BeEmpty();
    }

    [Fact]
    public async Task ReportsNoRestoreWhenTheOriginalIsStillInTheTrash()
    {
        var original = _immich.Add("PXL.mp4", "stale check", isTrashed: true);
        await ConvertAsync("stale check", original.Id);
        // The upload check says live; the asset itself says trashed.
        _immich.CheckAnswers[original.Checksum] = original.Id;

        var result = await CheckAsync();

        result.Restored.Should().BeEmpty();
        result.Issues.Should().BeEmpty();
        _immich.Trashes.Should().BeEmpty();
    }

    [Fact]
    public async Task ChecksConvertedChecksumsInBatchesAndTrashesReuploadsFromEach()
    {
        var reuploads = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            await ConvertAsync($"video {index}");
            if (index % 2 == 0) reuploads.Add(_immich.Add($"PXL_{index}.mp4", $"video {index}").Id);
        }

        var result = await CheckAsync(batchSize: 2);

        _immich.CheckBatches.Select(batch => batch.Count).Should().Equal(2, 2, 1);
        _immich.CheckBatches.SelectMany(batch => batch).Should().OnlyHaveUniqueItems().And.HaveCount(5);
        result.Trashed.Select(hit => hit.AssetId).Should().BeEquivalentTo(reuploads);
        // One trash call per batch, each made before the next batch is checked.
        _immich.Trashes.Should().HaveCount(3);
        _immich.Trashes.SelectMany(ids => ids).Should().Equal(reuploads);
    }

    [Fact]
    public async Task ReportsAReuploadAgainstTheLatestJobThatConvertedItsChecksum()
    {
        await ConvertAsync("converted twice");
        var latest = await ConvertAsync("converted twice");
        var reupload = _immich.Add("PXL.mp4", "converted twice");

        var result = await CheckAsync();

        _immich.CheckBatches.Should().ContainSingle().Which.Should().ContainSingle();
        result.Trashed.Should().ContainSingle().Which.Should().Match<ImmichReuploadHit>(hit => hit.JobId == latest.Id && hit.AssetId == reupload.Id);
    }

    [Fact]
    public async Task ChecksOnlyCompletedImmichJobs()
    {
        foreach (var status in new[] { JobStatus.Queued, JobStatus.ReadyToFinalize, JobStatus.Finalizing, JobStatus.Failed })
        {
            await ConvertAsync($"{status}", status: status);
            _immich.Add($"{status}.mp4", $"{status}");
        }

        var result = await CheckAsync();

        result.Checked.Should().Be(0);
        _immich.CheckBatches.Should().BeEmpty();
        _immich.Trashes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("different checksum")]
    [InlineData("trashed")]
    [InlineData("hidden")]
    [InlineData("locked")]
    [InlineData("external library")]
    [InlineData("image")]
    [InlineData("replacement")]
    [InlineData("open job")]
    public async Task LeavesAnAssetThatDoesNotConfirmTheUploadCheckAlone(string mismatch)
    {
        var job = await ConvertAsync("named");
        var named = _immich.Add("PXL.mp4", "named");
        var record = mismatch switch
        {
            "different checksum" => named with { Checksum = "other" },
            "trashed" => named with { IsTrashed = true },
            "hidden" => named with { Visibility = "hidden" },
            "locked" => named with { Visibility = "locked" },
            "external library" => named with { LibraryId = Guid.NewGuid().ToString() },
            "image" => named with { Type = "IMAGE" },
            _ => named
        };
        if (mismatch == "replacement")
        {
            job.ImmichNewAssetId = named.Id;
            await _repository.UpdateAsync(_settings.Database.Path, job);
        }

        if (mismatch == "open job") await ConvertAsync("other", named.Id, JobStatus.Encoding);

        // Immich's upload check still reports a live asset holding the checksum.
        _immich.CheckAnswers[named.Checksum] = named.Id;
        _immich.Replace(record);

        var result = await CheckAsync();

        result.Issues.Should().ContainSingle().Which.AssetId.Should().Be(named.Id);
        result.Trashed.Should().BeEmpty();
        _immich.Trashes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAssetImmichCannotReturnIsAnIssueAndOtherReuploadsAreStillTrashed(bool malformed)
    {
        await ConvertAsync("gone");
        await ConvertAsync("reuploaded");
        var gone = _immich.Add("gone.mp4", "gone");
        var reupload = _immich.Add("reuploaded.mp4", "reuploaded");
        _immich.Failures[gone.Id] = malformed
            ? new System.Text.Json.JsonException("The JSON value could not be converted to System.DateTimeOffset.")
            : new ImmichApiException("ImmichRequestFailed", "Immich answered 404 for asset.");

        var result = await CheckAsync();

        result.Issues.Should().ContainSingle().Which.AssetId.Should().Be(gone.Id);
        result.Trashed.Should().ContainSingle().Which.AssetId.Should().Be(reupload.Id);
    }

    [Fact]
    public async Task ARefusedBatchTrashIsRetriedOneAssetAtATime()
    {
        await ConvertAsync("vanished");
        await ConvertAsync("reuploaded");
        var vanished = _immich.Add("vanished.mp4", "vanished");
        var reupload = _immich.Add("reuploaded.mp4", "reuploaded");
        _immich.TrashFailures[vanished.Id] = new ImmichApiException("ImmichRequestFailed", "Immich answered 400 for DELETE /api/assets.");

        var result = await CheckAsync();

        result.Trashed.Should().ContainSingle().Which.AssetId.Should().Be(reupload.Id);
        result.Issues.Should().ContainSingle().Which.AssetId.Should().Be(vanished.Id);
        _immich.Get(vanished.Id).IsTrashed.Should().BeFalse();
    }

    [Fact]
    public async Task AnUnreachableImmichEndsTheRunAfterReportingWhatItTrashed()
    {
        await ConvertAsync("first");
        await ConvertAsync("second");
        var first = _immich.Add("first.mp4", "first");
        var second = _immich.Add("second.mp4", "second");
        _immich.TrashFailures[second.Id] = new ImmichApiException("ImmichUnavailable", "Immich did not answer.");

        var act = () => CheckAsync(batchSize: 1);

        await act.Should().ThrowAsync<ImmichApiException>().Where(exception => exception.Code == "ImmichUnavailable");
        _progress.Should().ContainSingle().Which.Should().Match<ScanProgress>(progress => progress.Stage == "Trashed" && progress.Path == "first.mp4");
        _immich.Get(first.Id).IsTrashed.Should().BeTrue();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private Task<ImmichReuploadGuardResult> CheckAsync() => CheckAsync(1000);

    private Task<ImmichReuploadGuardResult> CheckAsync(int batchSize) =>
        new ImmichReuploadGuard(_immich, _repository, batchSize).CheckAsync(_settings.Database.Path, _settings, new SynchronousProgress(_progress.Add));

    /// <summary>A finished Immich job for the content's checksum. Without an original ID, Immich has since purged the original.</summary>
    private Task<JobRecord> ConvertAsync(string content, string? originalId = null, JobStatus status = JobStatus.Completed)
    {
        var assetId = originalId ?? Guid.NewGuid().ToString();
        return _repository.CreateAsync(_settings.Database.Path, new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = Path.Combine(_settings.Immich.StagingDirectory, assetId, "PXL.mp4"),
            SourceFingerprint = "fingerprint",
            Status = status,
            CompletedUtc = status == JobStatus.Completed ? DateTimeOffset.UtcNow : null,
            SourceKind = JobSourceKind.Immich,
            ImmichAssetId = assetId,
            ImmichChecksum = FakeImmich.Checksum(content),
            ImmichNewAssetId = Guid.NewGuid().ToString()
        });
    }

    /// <summary>
    /// Holds assets by ID. The upload check answers from the assets themselves, as Immich does, unless
    /// <see cref="CheckAnswers"/> names the asset to report for a checksum.
    /// </summary>
    private sealed class FakeImmich : IImmichClientFactory, IImmichClient
    {
        private readonly List<ImmichAsset> _assets = [];

        public List<IReadOnlyList<string>> CheckBatches { get; } = [];
        public List<IReadOnlyList<string>> Trashes { get; } = [];
        public Dictionary<string, string> CheckAnswers { get; } = [];
        public Dictionary<string, Exception> Failures { get; } = [];
        public Dictionary<string, ImmichApiException> TrashFailures { get; } = [];

        public static string Checksum(string content) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));

        public ImmichAsset Add(string fileName, string content, bool isTrashed = false)
        {
            var asset = new ImmichAsset(
                Guid.NewGuid().ToString(), Checksum(content), fileName, ImmichValues.Video, ImmichValues.Timeline,
                IsFavorite: false, IsTrashed: isTrashed, LibraryId: null, LivePhotoVideoId: null,
                Taken, Taken, Taken, 60_000, new ImmichExif(100, Taken, null, null, null, null, "Google", "Pixel 7"), []);
            _assets.Add(asset);
            return asset;
        }

        public void Replace(ImmichAsset asset) => _assets[_assets.FindIndex(existing => existing.Id == asset.Id)] = asset;

        public ImmichAsset Get(string assetId) => _assets.Single(asset => asset.Id == assetId);

        public IImmichClient Create(ImmichSettings settings) => this;

        public Task<IReadOnlyList<ImmichUploadCheck>> CheckUploadsAsync(IReadOnlyList<string> checksums, CancellationToken cancellationToken = default)
        {
            CheckBatches.Add(checksums.ToArray());
            IReadOnlyList<ImmichUploadCheck> results = checksums.Select(checksum =>
            {
                var match = CheckAnswers.TryGetValue(checksum, out var answer)
                    ? _assets.Single(asset => asset.Id == answer) with { Checksum = checksum, IsTrashed = false }
                    : _assets.SingleOrDefault(asset => asset.Checksum == checksum);
                return match is null
                    ? new ImmichUploadCheck(checksum, false, null, null, false)
                    : new ImmichUploadCheck(checksum, true, "duplicate", match.Id, match.IsTrashed);
            }).ToList();
            return Task.FromResult(results);
        }

        public Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default) =>
            Failures.TryGetValue(assetId, out var failure) ? Task.FromException<ImmichAsset>(failure) : Task.FromResult(Get(assetId));

        public Task TrashAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default)
        {
            if (assetIds.FirstOrDefault(TrashFailures.ContainsKey) is { } failing) return Task.FromException(TrashFailures[failing]);
            Trashes.Add(assetIds.ToArray());
            foreach (var id in assetIds) Replace(Get(id) with { IsTrashed = true });
            return Task.CompletedTask;
        }

        public Task<ImmichServerVersion> GetServerVersionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ImmichTrashSettings Trash { get; set; } = new(Enabled: true, Days: 30);
        public Task<ImmichTrashSettings> GetTrashSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Trash);
        public Task<ImmichUser> GetCurrentUserAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ImmichAsset> SearchVideosAsync(DateTimeOffset takenBefore, bool includeFavorites, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadOriginalAsync(string assetId, string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichUploadResult> UploadAsync(ImmichUpload upload, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateMetadataAsync(string assetId, ImmichMetadataUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyRelationsAsync(string sourceAssetId, string targetAssetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TagAssetAsync(string assetId, IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Records reports on the calling thread; <see cref="Progress{T}"/> would post them later.</summary>
    private sealed class SynchronousProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
