using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Calibration;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Infrastructure.Calibration;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Scanning;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// <c>calibrate</c> as the CLI wires it (<see cref="CalibrationService.ReadOnly"/>) against a real database: what it reads,
/// and that it leaves the database file exactly as it found it.
/// </summary>
public sealed class CalibrationServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Old = Now.AddYears(-1);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly string _root;
    private readonly AppSettings _settings;
    private readonly FakeImmich _immich = new();

    public CalibrationServiceTests()
    {
        _root = Path.Combine(_directory, "videos");
        Directory.CreateDirectory(_root);
        _settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _root }] },
            Eligibility = new EligibilitySettings { Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "15Mbps", MinimumFileSize = "1B" }] },
            Quality = new QualitySettings { MinimumVmaf = 97 }
        };
    }

    [Fact]
    public async Task ARunReadsHistoryAndTheCacheButLeavesTheDatabaseFileByteForByteAsItWas()
    {
        var cached = await WriteVideoAsync("cached.mp4", "h264 20");
        var uncached = await WriteVideoAsync("new.mp4", "h264 12");
        await new SqliteMediaProbeCache(new SqliteDatabaseInitializer()).StoreAsync(_settings.Database.Path,
            new MediaProbeCacheEntry(cached, new FileInfo(cached).Length, File.GetLastWriteTimeUtc(cached).Ticks, Media("h264 20")));
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        await repository.CreateAsync(_settings.Database.Path, Job(JobStatus.Completed, QualityFingerprint.Compute(_settings), "h264", 40, saved: 75m));
        await repository.CreateAsync(_settings.Database.Path, Job(JobStatus.Completed, "older-settings", "h264", 40, saved: 75m));
        var before = Hash(_settings.Database.Path);
        var probe = new CountingProbe();

        var run = await Service(probe).RunAsync(_settings, null, null);

        Hash(_settings.Database.Path).Should().Be(before);
        probe.Probed.Should().Equal(uncached);
        run.ScanIssues.Should().Be(0);
        run.Report.Rules[0].Videos.Should().Be(1, "the cached file's facts came from the cache");
        run.Report.Rules[0].History!.Converted.Should().Be(1);
        run.Report.OtherSettings.Should().Be(1);
        run.Report.KeptOut.Should().ContainSingle().Which.Videos.Should().Be(1);
        (await new SqliteMediaProbeCache(new SqliteDatabaseInitializer()).LoadAllAsync(_settings.Database.Path)).Should().ContainSingle("the probe of new.mp4 is not saved");
    }

    [Fact]
    public async Task AnOutdatedDatabaseStopsTheRunWithoutBeingMigrated()
    {
        await using (var connection = new SqliteConnection($"Data Source={_settings.Database.Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL); INSERT INTO schema_migrations VALUES (7, 'then');";
            await command.ExecuteNonQueryAsync();
        }
        var before = Hash(_settings.Database.Path);

        var run = () => Service(new CountingProbe()).RunAsync(_settings, null, null);

        (await run.Should().ThrowAsync<DatabaseOutdatedException>()).Which.DatabasePath.Should().Be(_settings.Database.Path);
        Hash(_settings.Database.Path).Should().Be(before);
    }

    [Fact]
    public async Task WithoutADatabaseTheRunHasNoHistoryAndCreatesNone()
    {
        await WriteVideoAsync("a.mp4", "h264 20");

        var run = await Service(new CountingProbe()).RunAsync(_settings, null, null);

        File.Exists(_settings.Database.Path).Should().BeFalse();
        run.Report.Rules[0].Videos.Should().Be(1);
        run.Report.Rules[0].History.Should().BeNull();
    }

    [Fact]
    public async Task ImmichAssetsTakeTheFactsAJobOrSkipProbedFromTheSameFile()
    {
        _settings.Watch.Roots.Clear();
        _settings.Immich = new ImmichSettings { BaseUrl = "http://immich.test", SkipFavorites = true };
        var fromJob = _immich.Add("from-job.mp4", "c1");
        var fromSkip = _immich.Add("from-skip.mp4", "c2");
        var changed = _immich.Add("changed.mp4", "c3");
        var replacement = _immich.Add("replacement.mp4", "c4");
        _immich.Add("favourite.mp4", "c5", favourite: true);
        _immich.Add("photo-like.heic", "c6");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        await repository.CreateAsync(_settings.Database.Path, Job(JobStatus.Queued, null, "h264", 20, assetId: fromJob.Id, checksum: "c1"));
        await repository.CreateAsync(_settings.Database.Path, Job(JobStatus.Completed, null, "h264", 40, assetId: "original", checksum: "c0", replacement: replacement.Id));
        await repository.PutImmichSkipAsync(_settings.Database.Path, new ImmichSkipRecord(fromSkip.Id, "c2", "rules", "reason", Old, new VideoFacts("hevc", 3840, 2160, 25_000_000, 60, 1000)));
        await repository.PutImmichSkipAsync(_settings.Database.Path, new ImmichSkipRecord(changed.Id, "c-before", "rules", "reason", Old, new VideoFacts("hevc", 3840, 2160, 25_000_000, 60, 1000)));
        var searched = new List<int>();

        var run = await Service(new CountingProbe()).RunAsync(_settings, null, new SynchronousProgress<int>(searched.Add));

        var report = run.Report;
        report.Rules[0].Videos.Should().Be(1, "the queued job probed from-job.mp4 as h264 at 20 Mbps");
        report.KeptOut.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Codec = "hevc", Videos = 1 });
        report.CodecUnknown.Should().ContainSingle().Which.Videos.Should().Be(1, "changed.mp4's skip was probed from other bytes");
        report.AlreadyAv1Videos.Should().Be(1);
        searched.Should().Equal(1, 2, 3, 4);
        _immich.SearchedWithFavorites.Should().BeFalse();
        _immich.SearchedBefore.Should().Be(Now.AddDays(-_settings.Immich.MinimumAgeDays));
    }

    [Fact]
    public async Task AnAssetWithoutFactsIsBandedByItsSizeDurationAndDisplayedResolution()
    {
        _settings.Watch.Roots.Clear();
        _settings.Immich = new ImmichSettings { BaseUrl = "http://immich.test" };
        // 75 MB over 20 s is 30 Mbps; portrait 4K is still 4K.
        _immich.Add("PXL.mp4", "c1", sizeBytes: 75_000_000, durationMilliseconds: 20_000, width: 2160, height: 3840);

        var report = (await Service(new CountingProbe()).RunAsync(_settings, null, null)).Report;

        report.CodecUnknown.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Codec = (string?)null, Resolution = new ResolutionBand("4k+"), Bitrate = new BitrateBand(30, 40) });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private CalibrationService Service(IMediaProbe probe) => CalibrationService.ReadOnly(new ReadyFiles(), _ => probe, _immich, () => Now);

    /// <summary>A file whose content names its codec and Mbps, which <see cref="CountingProbe"/> reads back.</summary>
    private async Task<string> WriteVideoAsync(string name, string content)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, content);
        return Path.GetFullPath(path);
    }

    private static MediaInfo Media(string content)
    {
        var parts = content.Split(' ');
        return new MediaInfo(parts[0], 1, 1, 0, 0, 60, content.Length, 1920, 1080, long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * 1_000_000);
    }

    private static JobRecord Job(JobStatus status, string? fingerprint, string codec, int mbps, decimal? saved = null, string? assetId = null, string? checksum = null, string? replacement = null) => new()
    {
        Id = Guid.NewGuid(),
        SourcePath = $"source-{Guid.NewGuid():N}.mp4",
        SourceFingerprint = "fingerprint",
        Status = status,
        QualityFingerprint = fingerprint,
        PercentageSaved = saved,
        SourceKind = assetId is null ? JobSourceKind.File : JobSourceKind.Immich,
        ImmichAssetId = assetId,
        ImmichChecksum = checksum,
        ImmichNewAssetId = replacement,
        VideoFacts = new VideoFacts(codec, 1920, 1080, mbps * 1_000_000L, 60, 1000),
        CreatedUtc = Old,
        UpdatedUtc = Old
    };

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class ReadyFiles : IFileReadinessService
    {
        public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new FileReadinessResult(true, string.Empty));
    }

    private sealed class CountingProbe : IMediaProbe
    {
        public List<string> Probed { get; } = [];

        public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            Probed.Add(path);
            return Media(await File.ReadAllTextAsync(path, cancellationToken));
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeImmich : IImmichClientFactory, IImmichClient
    {
        private readonly List<ImmichAsset> _assets = [];

        public DateTimeOffset? SearchedBefore { get; private set; }
        public bool? SearchedWithFavorites { get; private set; }

        public ImmichAsset Add(string fileName, string checksum, bool favourite = false, long sizeBytes = 1000, long durationMilliseconds = 60_000, int width = 1920, int height = 1080)
        {
            var asset = new ImmichAsset(Guid.NewGuid().ToString(), checksum, fileName, ImmichValues.Video, ImmichValues.Timeline, favourite, IsTrashed: false, LibraryId: null,
                LivePhotoVideoId: null, Old, Old, Old, durationMilliseconds, new ImmichExif(sizeBytes, Old, null, null, null, null, null, null), [], width, height);
            _assets.Add(asset);
            return asset;
        }

        public IImmichClient Create(ImmichSettings settings) => this;

        public async IAsyncEnumerable<ImmichAsset> SearchVideosAsync(DateTimeOffset takenBefore, bool includeFavorites, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SearchedBefore = takenBefore;
            SearchedWithFavorites = includeFavorites;
            foreach (var asset in _assets)
            {
                await Task.Yield();
                yield return asset;
            }
        }

        public Task DownloadOriginalAsync(string assetId, string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException("calibrate downloads nothing");
        public Task<ImmichServerVersion> GetServerVersionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichTrashSettings> GetTrashSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichUser> GetCurrentUserAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichUploadResult> UploadAsync(ImmichUpload upload, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateMetadataAsync(string assetId, ImmichMetadataUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyRelationsAsync(string sourceAssetId, string targetAssetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TagAssetAsync(string assetId, IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TrashAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ImmichUploadCheck>> CheckUploadsAsync(IReadOnlyList<string> checksums, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
