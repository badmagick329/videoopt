using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// An Immich client and its factory in one, where every call throws <see cref="NotSupportedException"/> until a test double
/// overrides it. A double overrides only what its test calls, so a call the test did not expect fails loudly.
/// </summary>
internal class ImmichClientStub : IImmichClientFactory, IImmichClient
{
    public ImmichTrashSettings Trash { get; set; } = new(Enabled: true, Days: 30);

    public virtual IImmichClient Create(ImmichSettings settings) => this;

    public virtual Task<ImmichServerVersion> GetServerVersionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public virtual Task<ImmichUser> GetCurrentUserAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public virtual Task<ImmichTrashSettings> GetTrashSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Trash);
    public virtual IAsyncEnumerable<ImmichAsset> SearchVideosAsync(DateTimeOffset takenBefore, bool includeFavorites, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public virtual Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public virtual Task DownloadOriginalAsync(string assetId, string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public virtual Task<ImmichUploadResult> UploadAsync(ImmichUpload upload, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public virtual Task UpdateMetadataAsync(string assetId, ImmichMetadataUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public virtual Task CopyRelationsAsync(string sourceAssetId, string targetAssetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public virtual Task TagAssetAsync(string assetId, IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public virtual Task TrashAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<ImmichUploadCheck>> CheckUploadsAsync(IReadOnlyList<string> checksums, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// Reads the codec from the start of the downloaded file, so each fake asset's content decides its eligibility. "corrupt"
/// fails as ffprobe does on an unreadable file; "missing-ffprobe" fails as starting a missing executable does.
/// </summary>
internal sealed class ContentProbe : IMediaProbe
{
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var codec = (await File.ReadAllTextAsync(path, cancellationToken)).Split(' ')[0];
        if (codec == "corrupt") throw new MediaProbeException("ffprobe exited with 1: Invalid data found when processing input");
        if (codec == "missing-ffprobe") throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified.");
        return new MediaInfo(codec, 1, 1, 0, 0, 60, new FileInfo(path).Length, 1920, 1080, 20_000_000);
    }
}

/// <summary>
/// Immich in memory: video assets and their bytes by ID, with the checksums Immich computes (base64 SHA-1 of the content).
/// The search returns every asset unless <see cref="FiltersSearch"/> is set, because the code under test must re-check each
/// result; an upload answers as a duplicate of any asset holding the same bytes; the upload check names the asset holding a
/// checksum unless <see cref="CheckAnswers"/> says otherwise. Each call that changes or reads Immich leaves a record.
/// </summary>
internal sealed class InMemoryImmich(DateTimeOffset taken) : ImmichClientStub
{
    private readonly List<ImmichAsset> _assets = [];
    private readonly Dictionary<string, string> _contents = [];

    /// <summary>Whether the search leaves out trashed assets and those taken after its cutoff, as Immich's filter does.</summary>
    public bool FiltersSearch { get; init; }

    public Exception? FailSearch { get; set; }

    /// <summary>Thrown when that asset's bytes are downloaded or its record is read.</summary>
    public Dictionary<string, Exception> Failures { get; } = [];

    /// <summary>Thrown when a trash call includes that asset.</summary>
    public Dictionary<string, ImmichApiException> TrashFailures { get; } = [];

    /// <summary>The asset the upload check names for a checksum, whatever the assets themselves say.</summary>
    public Dictionary<string, string> CheckAnswers { get; } = [];

    public List<string> Downloads { get; } = [];
    public List<IReadOnlyList<string>> CheckBatches { get; } = [];
    public List<IReadOnlyList<string>> Trashes { get; } = [];
    public DateTimeOffset? SearchedBefore { get; private set; }
    public bool? SearchedWithFavorites { get; private set; }

    /// <summary>Immich's checksum of a file: the base64 SHA-1 of its bytes.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "Immich's asset checksum is SHA-1.")]
    public static string Checksum(string content) => Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(content)));

    public ImmichAsset Add(string fileName, string content, bool isTrashed = false)
    {
        var asset = new ImmichAsset(
            Guid.NewGuid().ToString(), Checksum(content), fileName, ImmichValues.Video, ImmichValues.Timeline,
            IsFavorite: false, IsTrashed: isTrashed, LibraryId: null, LivePhotoVideoId: null,
            taken, taken, taken, 60_000, new ImmichExif(Encoding.UTF8.GetByteCount(content), taken, null, null, null, null, "Google", "Pixel 7"), []);
        _assets.Add(asset);
        _contents[asset.Id] = content;
        return asset;
    }

    public ImmichAsset Get(string assetId) => _assets.Single(asset => asset.Id == assetId);

    /// <summary>Replaces the asset with the same ID, or else the one with the same file name, keeping its content unless new content is given.</summary>
    public void Replace(ImmichAsset asset, string? content = null)
    {
        var index = _assets.FindIndex(existing => existing.Id == asset.Id);
        if (index < 0) index = _assets.FindIndex(existing => existing.OriginalFileName == asset.OriginalFileName);
        _contents[asset.Id] = content ?? _contents[_assets[index].Id];
        _assets[index] = content is null ? asset : asset with { Checksum = Checksum(content), Exif = asset.Exif with { FileSizeInBytes = Encoding.UTF8.GetByteCount(content) } };
    }

    public void Remove(string assetId) => _assets.RemoveAll(asset => asset.Id == assetId);

    /// <summary>Makes the search return the asset a second time, as a result page overlapping the next can.</summary>
    public void Duplicate(ImmichAsset asset) => _assets.Add(asset);

    public override async IAsyncEnumerable<ImmichAsset> SearchVideosAsync(DateTimeOffset takenBefore, bool includeFavorites, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SearchedBefore = takenBefore;
        SearchedWithFavorites = includeFavorites;
        if (FailSearch is { } failure) throw failure;
        foreach (var asset in _assets.Where(asset => !FiltersSearch || (!asset.IsTrashed && asset.FileCreatedAt < takenBefore)).ToArray())
        {
            await Task.Yield();
            yield return asset;
        }
    }

    public override Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default) =>
        Failures.TryGetValue(assetId, out var failure) ? Task.FromException<ImmichAsset>(failure)
        : _assets.SingleOrDefault(asset => asset.Id == assetId) is { } asset ? Task.FromResult(asset)
        : Task.FromException<ImmichAsset>(new ImmichApiException("ImmichRequestFailed", $"Immich returned HTTP 404 for GET /api/assets/{assetId}."));

    public override async Task DownloadOriginalAsync(string assetId, string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        Downloads.Add(assetId);
        if (Failures.TryGetValue(assetId, out var failure)) throw failure;
        await File.WriteAllTextAsync(destinationPath, _contents[assetId], cancellationToken);
    }

    public override async Task<ImmichUploadResult> UploadAsync(ImmichUpload upload, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var content = await File.ReadAllTextAsync(upload.FilePath, cancellationToken);
        if (_assets.FirstOrDefault(asset => asset.Checksum == Checksum(content)) is { } existing) return new ImmichUploadResult(existing.Id, IsDuplicate: true);
        return new ImmichUploadResult(Add(upload.FileName, content).Id, IsDuplicate: false);
    }

    public override Task UpdateMetadataAsync(string assetId, ImmichMetadataUpdate update, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public override Task CopyRelationsAsync(string sourceAssetId, string targetAssetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public override Task TagAssetAsync(string assetId, IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override Task TrashAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default)
    {
        if (assetIds.FirstOrDefault(TrashFailures.ContainsKey) is { } failing) return Task.FromException(TrashFailures[failing]);
        Trashes.Add(assetIds.ToArray());
        foreach (var id in assetIds) Replace(Get(id) with { IsTrashed = true });
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyList<ImmichUploadCheck>> CheckUploadsAsync(IReadOnlyList<string> checksums, CancellationToken cancellationToken = default)
    {
        CheckBatches.Add(checksums.ToArray());
        IReadOnlyList<ImmichUploadCheck> results = checksums.Select(checksum =>
        {
            var match = CheckAnswers.TryGetValue(checksum, out var answer)
                ? _assets.Single(asset => asset.Id == answer) with { Checksum = checksum, IsTrashed = false }
                : _assets.FirstOrDefault(asset => asset.Checksum == checksum);
            return match is null
                ? new ImmichUploadCheck(checksum, Rejected: false, null, null, IsTrashed: false)
                : new ImmichUploadCheck(checksum, Rejected: true, "duplicate", match.Id, match.IsTrashed);
        }).ToList();
        return Task.FromResult(results);
    }
}
