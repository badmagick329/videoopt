using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Immich;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class ImmichDiscoveryServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Old = Now.AddDays(-200);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeImmich _immich = new();
    private readonly AppSettings _settings;

    public ImmichDiscoveryServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Immich = new ImmichSettings { BaseUrl = "http://immich.test:2283", StagingDirectory = Path.Combine(_directory, "staging") },
            Eligibility = new EligibilitySettings { Rules = [HevcRule()] }
        };
    }

    [Fact]
    public async Task QueuesAnEligibleVideoAsAnImmichJobUnderItsStagingFolder()
    {
        var asset = _immich.Add("PXL_20250101_120000000.mp4", "hevc video");

        var result = await DiscoverAsync();

        var job = result.Queued.Should().ContainSingle().Subject;
        job.SourceKind.Should().Be(JobSourceKind.Immich);
        job.ImmichAssetId.Should().Be(asset.Id);
        job.ImmichChecksum.Should().Be(asset.Checksum);
        job.SourcePath.Should().Be(Path.Combine(_settings.Immich.StagingDirectory, asset.Id, "PXL_20250101_120000000.mp4"));
        job.ExecutionMode.Should().Be(ProcessingModes.Local);
        (await File.ReadAllTextAsync(job.SourcePath)).Should().Be("hevc video");
        (await _repository.GetAsync(_settings.Database.Path, job.Id))!.Status.Should().Be(JobStatus.Queued);
        _immich.SearchedBefore.Should().Be(Now.AddDays(-90));
        _immich.SearchedWithFavorites.Should().BeFalse();
    }

    [Theory]
    [InlineData("image")]
    [InlineData("too new")]
    [InlineData("favourite")]
    [InlineData("trashed")]
    [InlineData("hidden")]
    [InlineData("locked")]
    [InlineData("external library")]
    public async Task ReChecksEverySearchResultAndLeavesMismatchesAlone(string mismatch)
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        _immich.Replace(mismatch switch
        {
            "image" => asset with { Type = "IMAGE" },
            "too new" => asset with { FileCreatedAt = Now.AddDays(-89) },
            "favourite" => asset with { IsFavorite = true },
            "trashed" => asset with { IsTrashed = true },
            "hidden" => asset with { Visibility = "hidden" },
            "locked" => asset with { Visibility = "locked" },
            _ => asset with { LibraryId = Guid.NewGuid().ToString() }
        });

        var result = await DiscoverAsync();

        result.Excluded.Should().Be(1);
        result.Queued.Should().BeEmpty();
        _immich.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task QueuesAFavouriteWhenSkipFavoritesIsOff()
    {
        _settings.Immich.SkipFavorites = false;
        var asset = _immich.Add("clip.mp4", "hevc video");
        _immich.Replace(asset with { IsFavorite = true, Visibility = ImmichValues.Archive });

        var result = await DiscoverAsync();

        result.Queued.Should().ContainSingle();
        _immich.SearchedWithFavorites.Should().BeTrue();
    }

    [Fact]
    public async Task LeavesAssetsWithOpenOrCompletedJobsWithoutDownloadingThem()
    {
        var open = _immich.Add("open.mp4", "hevc open");
        var converted = _immich.Add("converted.mp4", "hevc converted");
        var reuploaded = _immich.Add("reuploaded.mp4", "hevc reuploaded");
        await CreateJobAsync(open, JobStatus.Encoding);
        await CreateJobAsync(converted, JobStatus.Completed);
        // The phone uploaded a converted original again: a new asset ID with the old checksum.
        await CreateJobAsync(reuploaded with { Id = Guid.NewGuid().ToString() }, JobStatus.Completed);

        var result = await DiscoverAsync();

        result.AlreadyQueued.Should().Be(1);
        result.AlreadyConverted.Should().Be(2);
        result.Queued.Should().BeEmpty();
        _immich.Downloads.Should().BeEmpty();
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Finalizing)]
    [InlineData(JobStatus.Failed)]
    public async Task LeavesTheAv1AssetThatFinalisationUploadedAlone(JobStatus status)
    {
        var original = _immich.Add("PXL.mp4", "hevc original");
        var replacement = _immich.Add("PXL.av1.mp4", "hevc replacement");
        _immich.Remove(original);
        var job = await CreateJobAsync(original, status, status == JobStatus.Failed ? "FinalisationFailed" : null);
        job.ImmichNewAssetId = replacement.Id;
        await _repository.UpdateAsync(_settings.Database.Path, job);

        var result = await DiscoverAsync();

        result.AlreadyConverted.Should().Be(1);
        _immich.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task JudgesAnAssetTheSearchReturnsTwiceOnce()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        _immich.Duplicate(asset);

        var result = await DiscoverAsync();

        result.Queued.Should().ContainSingle();
        _immich.Downloads.Should().ContainSingle();
        File.Exists(result.Queued[0].SourcePath).Should().BeTrue();
    }

    [Theory]
    [InlineData(JobStatus.Failed, "EncodeFailed", false)]
    [InlineData(JobStatus.Cancelled, "Cancelled", false)]
    [InlineData(JobStatus.Failed, "SourceNotReady", true)]
    [InlineData(JobStatus.Failed, "Ineligible", true)]
    public async Task RequeuesAFailedAssetOnlyWhenTheFailureJudgedItsStateAtRunTime(JobStatus status, string category, bool requeued)
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        await CreateJobAsync(asset, status, category);

        var result = await DiscoverAsync();

        result.Queued.Should().HaveCount(requeued ? 1 : 0);
        result.PreviouslyFailed.Should().Be(requeued ? 0 : 1);
    }

    [Fact]
    public async Task JudgesAFailedAssetByItsLatestJob()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        await CreateJobAsync(asset, JobStatus.Failed, "EncodeFailed");
        await CreateJobAsync(asset, JobStatus.Failed, "SourceNotReady");

        var result = await DiscoverAsync();

        result.Queued.Should().ContainSingle();
    }

    [Fact]
    public async Task RequeuesAFailedAssetWhoseChecksumChanged()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        await CreateJobAsync(asset with { Checksum = "earlier-checksum" }, JobStatus.Failed, "EncodeFailed");

        var result = await DiscoverAsync();

        result.Queued.Should().ContainSingle();
    }

    [Fact]
    public async Task RecordsAnIneligibleAssetAsASkipAndDeletesItsStagedCopy()
    {
        var asset = _immich.Add("clip.mp4", "h264 video");

        var result = await DiscoverAsync();

        result.Skipped.Should().Be(1);
        result.Queued.Should().BeEmpty();
        Directory.Exists(Path.Combine(_settings.Immich.StagingDirectory, asset.Id)).Should().BeFalse();
        var skip = (await _repository.ListImmichSkipsAsync(_settings.Database.Path))[asset.Id];
        skip.Checksum.Should().Be(asset.Checksum);
        skip.RulesFingerprint.Should().Be(EligibilityEvaluator.RulesFingerprint(_settings.Eligibility));
        skip.Reason.Should().Contain("codec is h264");
        skip.RecordedUtc.Should().Be(Now);
    }

    [Fact]
    public async Task ASecondRunQueuesNothingNewAndDownloadsNothingAlreadyJudged()
    {
        _immich.Add("eligible.mp4", "hevc video");
        _immich.Add("ineligible.mp4", "h264 video");

        var first = await DiscoverAsync();
        var second = await DiscoverAsync();

        File.Exists(first.Queued.Should().ContainSingle().Subject.SourcePath).Should().BeTrue();
        second.Queued.Should().BeEmpty();
        second.AlreadyQueued.Should().Be(1);
        second.PreviouslySkipped.Should().Be(1);
        _immich.Downloads.Should().HaveCount(2);
        (await _repository.ListAsync(_settings.Database.Path, terminal: false)).Should().ContainSingle();
    }

    [Fact]
    public async Task AChangedChecksumMakesASkipStale()
    {
        var asset = _immich.Add("clip.mp4", "h264 video");
        await DiscoverAsync();
        _immich.Replace(asset, "hevc edited video");

        var result = await DiscoverAsync();

        result.Queued.Should().ContainSingle();
        _immich.Downloads.Should().HaveCount(2);
    }

    [Fact]
    public async Task ChangedRulesMakeASkipStale()
    {
        _immich.Add("clip.mp4", "h264 video");
        await DiscoverAsync();
        _settings.Eligibility.Rules.Add(new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "10Mbps", MinimumFileSize = "5B" });

        var result = await DiscoverAsync();

        result.Queued.Should().ContainSingle();
        _immich.Downloads.Should().HaveCount(2);
    }

    [Fact]
    public async Task RejectsTooSmallOrWrongExtensionAssetsWithoutDownloadingThem()
    {
        var small = _immich.Add("small.mp4", "hevc video");
        _immich.Replace(small with { Exif = small.Exif with { FileSizeInBytes = 3 } });
        _immich.Add("clip.avi", "hevc video");
        var unknownSize = _immich.Add("unknown-size.mp4", "hevc video");
        _immich.Replace(unknownSize with { Exif = unknownSize.Exif with { FileSizeInBytes = null } });

        var result = await DiscoverAsync();

        result.Ineligible.Should().Be(2);
        result.Queued.Should().ContainSingle().Which.ImmichAssetId.Should().Be(unknownSize.Id);
        _immich.Downloads.Should().Equal(unknownSize.Id);
    }

    [Fact]
    public async Task FirstQueuesOneAssetAndStopsDownloading()
    {
        _immich.Add("one.mp4", "hevc one");
        _immich.Add("two.mp4", "hevc two");

        var result = await DiscoverAsync(first: true);

        result.Queued.Should().ContainSingle();
        _immich.Downloads.Should().ContainSingle();
    }

    [Fact]
    public async Task FirstQueuesNothingNewWhileAVideoIsAlreadyQueued()
    {
        var open = _immich.Add("open.mp4", "hevc open");
        _immich.Add("next.mp4", "hevc next");
        await CreateJobAsync(open, JobStatus.Queued);

        var result = await DiscoverAsync(first: true);

        result.Queued.Should().BeEmpty();
        result.AlreadyQueued.Should().Be(1);
        _immich.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchItsChecksumIsAnIssue()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        _immich.Replace(asset with { Checksum = Sha1("something else") });

        var result = await DiscoverAsync();

        result.Issues.Should().Be(1);
        result.Queued.Should().BeEmpty();
        Directory.Exists(Path.Combine(_settings.Immich.StagingDirectory, asset.Id)).Should().BeFalse();
        (await _repository.ListImmichSkipsAsync(_settings.Database.Path)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("not-a-guid", "clip.mp4")]
    [InlineData("{id}", "clip.mp4")]
    [InlineData(" id", "clip.mp4")]
    [InlineData("..", "clip.mp4")]
    [InlineData(null, @"..\escape.mp4")]
    [InlineData(null, "nested/clip.mp4")]
    [InlineData(null, @"C:\escape.mp4")]
    [InlineData(null, "clip.mp4:stream.mp4")]
    public async Task NeverStagesOutsideTheAssetFolder(string? id, string fileName)
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        _immich.Replace(asset with { Id = id?.Replace("id", asset.Id, StringComparison.Ordinal) ?? asset.Id, OriginalFileName = fileName });

        var result = await DiscoverAsync();

        result.Issues.Should().Be(1);
        _immich.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task ARequestFailureForOneAssetMovesOnToTheNext()
    {
        var gone = _immich.Add("gone.mp4", "hevc gone");
        _immich.Add("next.mp4", "hevc next");
        _immich.Failures[gone.Id] = new ImmichApiException("ImmichRequestFailed", "Immich answered 404.");

        var result = await DiscoverAsync();

        result.Issues.Should().Be(1);
        result.Queued.Should().ContainSingle();
    }

    [Fact]
    public async Task AnUnreachableImmichEndsTheRun()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        _immich.Failures[asset.Id] = new ImmichApiException("ImmichUnavailable", "Immich is unreachable.");

        var action = async () => await DiscoverAsync();

        await action.Should().ThrowAsync<ImmichApiException>().Where(exception => exception.Code == "ImmichUnavailable");
        Directory.Exists(Path.Combine(_settings.Immich.StagingDirectory, asset.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task AFileFfprobeCannotReadIsAnIssueAndTheRunGoesOn()
    {
        var unreadable = _immich.Add("unreadable.mp4", "corrupt video");
        _immich.Add("next.mp4", "hevc next");

        var result = await DiscoverAsync();

        result.Issues.Should().Be(1);
        result.Queued.Should().ContainSingle();
        Directory.Exists(Path.Combine(_settings.Immich.StagingDirectory, unreadable.Id)).Should().BeFalse();
        (await _repository.ListImmichSkipsAsync(_settings.Database.Path)).Should().BeEmpty();
    }

    [Fact]
    public async Task AMissingFfprobeEndsTheRunAfterOneDownload()
    {
        var asset = _immich.Add("clip.mp4", "missing-ffprobe video");
        _immich.Add("next.mp4", "hevc next");

        var action = async () => await DiscoverAsync();

        await action.Should().ThrowAsync<System.ComponentModel.Win32Exception>();
        _immich.Downloads.Should().ContainSingle();
        Directory.Exists(Path.Combine(_settings.Immich.StagingDirectory, asset.Id)).Should().BeFalse();
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Interrupted)]
    public async Task DownloadsAgainTheMissingStagedOriginalOfAnOpenJob(JobStatus status)
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        var job = await CreateJobAsync(asset, status, staged: false);

        var result = await DiscoverAsync();

        result.Restaged.Should().Be(1);
        result.AlreadyQueued.Should().Be(1);
        result.Queued.Should().BeEmpty();
        _immich.Downloads.Should().Equal(asset.Id);
        (await File.ReadAllTextAsync(job.SourcePath)).Should().Be("hevc video");
        (await _repository.GetAsync(_settings.Database.Path, job.Id))!.Status.Should().Be(status);
    }

    [Fact]
    public async Task LeavesTheStagedOriginalOfAReadyJobAlone()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        await CreateJobAsync(asset, JobStatus.ReadyToFinalize, staged: false);

        var result = await DiscoverAsync();

        result.Restaged.Should().Be(0);
        _immich.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task FailsAnOpenJobWhoseMissingOriginalImmichNoLongerReturns()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        var job = await CreateJobAsync(asset, JobStatus.Queued, staged: false);
        _immich.Remove(asset);
        _immich.Failures[asset.Id] = new ImmichApiException("ImmichRequestFailed", "Immich answered 404.");

        var result = await DiscoverAsync();

        result.Issues.Should().Be(1);
        var stored = (await _repository.GetAsync(_settings.Database.Path, job.Id))!;
        stored.Status.Should().Be(JobStatus.Failed);
        stored.FailureCategory.Should().Be("SourceMissing");
        Directory.Exists(Path.Combine(_settings.Immich.StagingDirectory, asset.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task FailsAnOpenJobWhoseAssetChangedAndQueuesTheAssetAfresh()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        var job = await CreateJobAsync(asset with { Checksum = Sha1("hevc earlier video") }, JobStatus.Queued, staged: false);

        var result = await DiscoverAsync();

        result.Restaged.Should().Be(0);
        result.Issues.Should().Be(1);
        (await _repository.GetAsync(_settings.Database.Path, job.Id))!.FailureCategory.Should().Be("SourceMissing");
        var queued = result.Queued.Should().ContainSingle().Subject;
        queued.ImmichChecksum.Should().Be(asset.Checksum);
        (await File.ReadAllTextAsync(queued.SourcePath)).Should().Be("hevc video");
    }

    [Fact]
    public async Task FirstDownloadsAgainOnlyTheOldestOpenJobsOriginal()
    {
        var older = _immich.Add("older.mp4", "hevc older");
        var newer = _immich.Add("newer.mp4", "hevc newer");
        await CreateJobAsync(older, JobStatus.Queued, staged: false);
        await Task.Delay(20);
        await CreateJobAsync(newer, JobStatus.Queued, staged: false);

        var result = await DiscoverAsync(first: true);

        result.Restaged.Should().Be(1);
        _immich.Downloads.Should().Equal(older.Id);
    }

    [Fact]
    public async Task NeverDownloadsAgainToAPathOutsideTheAssetFolder()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        var job = await CreateJobAsync(asset, JobStatus.Queued, staged: false);
        job.SourcePath = Path.Combine(_directory, "elsewhere", "clip.mp4");
        await _repository.UpdateAsync(_settings.Database.Path, job);

        var result = await DiscoverAsync();

        result.Issues.Should().Be(1);
        (await _repository.GetAsync(_settings.Database.Path, job.Id))!.FailureCategory.Should().Be("SourceMissing");
        File.Exists(job.SourcePath).Should().BeFalse();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private Task<ImmichDiscoveryResult> DiscoverAsync(bool first = false) =>
        new ImmichDiscoveryService(_immich, _repository, _ => new ContentProbe(), new FileFingerprintService(), () => Now)
            .DiscoverAsync(_settings.Database.Path, _settings, first);

    /// <summary>An Immich job for the asset. An open one has its staged copy unless <paramref name="staged"/> is false.</summary>
    private async Task<JobRecord> CreateJobAsync(ImmichAsset asset, JobStatus status, string? failureCategory = null, bool staged = true)
    {
        var path = Path.Combine(_settings.Immich.StagingDirectory, asset.Id, asset.OriginalFileName);
        if (staged && status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "staged");
        }

        return await _repository.CreateAsync(_settings.Database.Path, new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = path,
            SourceFingerprint = "fingerprint",
            Status = status,
            FailureCategory = failureCategory,
            SourceKind = JobSourceKind.Immich,
            ImmichAssetId = asset.Id,
            ImmichChecksum = asset.Checksum
        });
    }

    private static EligibilityRuleSettings HevcRule() => new() { Codecs = ["hevc"], Resolution = "1080p-1440p", MinimumVideoBitrate = "10Mbps", MinimumFileSize = "5B" };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "Immich's asset checksum is SHA-1.")]
    private static string Sha1(string content) => Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// Reads the codec from the start of the staged file, so each fake asset's content decides its eligibility. "corrupt"
    /// fails as ffprobe does on an unreadable file; "missing-ffprobe" fails as starting a missing executable does.
    /// </summary>
    private sealed class ContentProbe : IMediaProbe
    {
        public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            var codec = (await File.ReadAllTextAsync(path, cancellationToken)).Split(' ')[0];
            if (codec == "corrupt") throw new InvalidDataException("ffprobe exited with 1: Invalid data found when processing input");
            if (codec == "missing-ffprobe") throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified.");
            return new MediaInfo(codec, 1, 1, 0, 0, 60, new FileInfo(path).Length, 1920, 1080, 20_000_000);
        }
    }

    private sealed class FakeImmich : IImmichClientFactory, IImmichClient
    {
        private readonly List<ImmichAsset> _assets = [];
        private readonly Dictionary<string, string> _contents = [];

        public List<string> Downloads { get; } = [];
        public Dictionary<string, ImmichApiException> Failures { get; } = [];
        public DateTimeOffset? SearchedBefore { get; private set; }
        public bool? SearchedWithFavorites { get; private set; }

        public ImmichAsset Add(string fileName, string content)
        {
            var asset = new ImmichAsset(
                Guid.NewGuid().ToString(), Sha1(content), fileName, ImmichValues.Video, ImmichValues.Timeline,
                IsFavorite: false, IsTrashed: false, LibraryId: null, LivePhotoVideoId: null,
                Old, Old, Old, 60_000, new ImmichExif(Encoding.UTF8.GetByteCount(content), Old, null, null, null, null, "Google", "Pixel 7"), []);
            _assets.Add(asset);
            _contents[asset.Id] = content;
            return asset;
        }

        /// <summary>Replaces the asset found at the same position, keeping its content unless new content is given.</summary>
        public void Replace(ImmichAsset asset, string? content = null)
        {
            var index = _assets.FindIndex(existing => existing.OriginalFileName == asset.OriginalFileName || existing.Id == asset.Id);
            var previous = _assets[index];
            _contents[asset.Id] = content ?? _contents[previous.Id];
            _assets[index] = content is null ? asset : asset with { Checksum = Sha1(content), Exif = asset.Exif with { FileSizeInBytes = Encoding.UTF8.GetByteCount(content) } };
        }

        public void Remove(ImmichAsset asset) => _assets.RemoveAll(existing => existing.Id == asset.Id);

        public void Duplicate(ImmichAsset asset) => _assets.Add(asset);

        public IImmichClient Create(ImmichSettings settings) => this;

        public async IAsyncEnumerable<ImmichAsset> SearchVideosAsync(DateTimeOffset takenBefore, bool includeFavorites, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SearchedBefore = takenBefore;
            SearchedWithFavorites = includeFavorites;
            foreach (var asset in _assets.ToArray())
            {
                await Task.Yield();
                yield return asset;
            }
        }

        public async Task DownloadOriginalAsync(string assetId, string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            Downloads.Add(assetId);
            if (Failures.TryGetValue(assetId, out var failure)) throw failure;
            await File.WriteAllTextAsync(destinationPath, _contents[assetId], cancellationToken);
        }

        public Task<ImmichServerVersion> GetServerVersionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
