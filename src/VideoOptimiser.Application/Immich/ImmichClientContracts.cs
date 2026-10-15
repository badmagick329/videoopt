using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Immich;

public sealed record ImmichServerVersion(int Major, int Minor, int Patch)
{
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public sealed record ImmichUser(string Id, string Name);

/// <summary>
/// Immich's trash as the server reports it. Immich's deletion check purges trashed assets older than <see cref="Days"/>, and
/// with the trash off it uses a cutoff of zero days, so anything trashed is deleted for good at its next run.
/// </summary>
public sealed record ImmichTrashSettings(bool Enabled, int Days)
{
    public bool IsOn => Enabled && Days > 0;
}

/// <summary>
/// The precondition of every trash call. Finalisation and the re-upload guard trash rather than delete because the trash is
/// the user's undo and a trashed original's checksum keeps the phone from uploading it again; with the trash off, both
/// would delete originals permanently instead.
/// </summary>
public static class ImmichTrash
{
    public const string DisabledCode = "ImmichTrashDisabled";

    /// <summary>Below this many days, doctor warns that a mistake noticed later cannot be undone from Immich's trash.</summary>
    public const int ShortDays = 7;

    public const string DisabledMessage = "Immich's trash is off, so a video VideoOptimiser moves to the trash would be deleted for good at Immich's next deletion check. Turn the trash on in Immich under Administration > Settings > Trash Settings, then run again.";

    public static async Task RequireOnAsync(IImmichClient client, CancellationToken cancellationToken = default)
    {
        if (!(await client.GetTrashSettingsAsync(cancellationToken)).IsOn) throw new ImmichApiException(DisabledCode, DisabledMessage);
    }
}

/// <summary>
/// The fields of an Immich asset the Immich source reads. <see cref="Checksum"/> is Immich's base64 SHA-1 of the original
/// file, which the phone also uses to decide whether a file is already backed up. <see cref="TagIds"/> is filled only by
/// <see cref="IImmichClient.GetAssetAsync"/>; search results leave it empty.
/// </summary>
public sealed record ImmichAsset(
    string Id,
    string Checksum,
    string OriginalFileName,
    string Type,
    string Visibility,
    bool IsFavorite,
    bool IsTrashed,
    string? LibraryId,
    string? LivePhotoVideoId,
    DateTimeOffset FileCreatedAt,
    DateTimeOffset FileModifiedAt,
    DateTimeOffset LocalDateTime,
    long? DurationMilliseconds,
    ImmichExif Exif,
    IReadOnlyList<string> TagIds,
    int? Width = null,
    int? Height = null);

public sealed record ImmichExif(
    long? FileSizeInBytes,
    DateTimeOffset? DateTimeOriginal,
    double? Latitude,
    double? Longitude,
    string? Description,
    int? Rating,
    string? Make,
    string? Model);

/// <summary>Values the API spells as strings.</summary>
public static class ImmichValues
{
    public const string Video = "VIDEO";
    public const string Timeline = "timeline";
    public const string Archive = "archive";
}

/// <summary>A file to upload as a new asset. Dates come from the original asset so the new one sorts in its place.</summary>
public sealed record ImmichUpload(string FilePath, string FileName, DateTimeOffset FileCreatedAt, DateTimeOffset FileModifiedAt, bool IsFavorite);

/// <summary>
/// <see cref="IsDuplicate"/> means Immich already held a file with the same checksum and returned that asset's ID, which
/// lets an interrupted finalisation resume without uploading twice.
/// </summary>
public sealed record ImmichUploadResult(string AssetId, bool IsDuplicate);

/// <summary>Fields to set on an asset; a null field is left unchanged.</summary>
public sealed record ImmichMetadataUpdate(
    string? Description = null,
    int? Rating = null);

/// <summary>
/// Immich's answer to "would this checksum upload?". <see cref="Rejected"/> with <see cref="IsTrashed"/> still set means a
/// trashed asset holds the checksum, which is what keeps the phone from uploading a converted original again.
/// </summary>
public sealed record ImmichUploadCheck(string Checksum, bool Rejected, string? Reason, string? ExistingAssetId, bool IsTrashed);

public interface IImmichClient
{
    Task<ImmichServerVersion> GetServerVersionAsync(CancellationToken cancellationToken = default);
    Task<ImmichUser> GetCurrentUserAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads Immich's trash setting; needs no key. Read once per client: each run or finalisation creates its own.</summary>
    Task<ImmichTrashSettings> GetTrashSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Videos taken before <paramref name="takenBefore"/> on the timeline or in the archive, not trashed and not from an
    /// external library, oldest first. Callers must still re-check each result: a wrong match ends with an original in the trash.
    /// </summary>
    IAsyncEnumerable<ImmichAsset> SearchVideosAsync(DateTimeOffset takenBefore, bool includeFavorites, CancellationToken cancellationToken = default);

    Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the original file to <paramref name="destinationPath"/>. The bytes land in a <c>.part</c> file that is renamed
    /// only once complete, so the final name never holds a partial download.
    /// </summary>
    Task DownloadOriginalAsync(string assetId, string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default);

    Task<ImmichUploadResult> UploadAsync(ImmichUpload upload, IProgress<long>? progress = null, CancellationToken cancellationToken = default);
    Task UpdateMetadataAsync(string assetId, ImmichMetadataUpdate update, CancellationToken cancellationToken = default);

    /// <summary>Copies albums, favourite, shared links, stack and sidecar from one asset to another.</summary>
    Task CopyRelationsAsync(string sourceAssetId, string targetAssetId, CancellationToken cancellationToken = default);

    Task TagAssetAsync(string assetId, IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves assets to Immich's trash. Never purges: the trash is the user's undo and keeps the checksums known. Refuses with
    /// <see cref="ImmichTrash.DisabledCode"/> while the trash is off, whatever the caller checked before.
    /// </summary>
    Task TrashAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImmichUploadCheck>> CheckUploadsAsync(IReadOnlyList<string> checksums, CancellationToken cancellationToken = default);
}

public interface IImmichClientFactory
{
    IImmichClient Create(ImmichSettings settings);
}

/// <param name="Failed">The jobs marked failed because their staged copy could not be brought back.</param>
/// <param name="Unusable">Why Immich can't be used, when it stopped the restaging; null when every job was tried.</param>
public sealed record ImmichRestageResult(int Restaged, IReadOnlyList<JobRecord> Failed, string? Unusable = null);

/// <summary>
/// An Immich request that failed, with a message that says what to fix. <see cref="Code"/> is the diagnostic code doctor
/// reports. The message names the key's environment variable, never the key.
/// </summary>
public sealed class ImmichApiException(string code, string message, int? statusCode = null) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>The HTTP status of a request Immich answered with an error; null when it never answered.</summary>
    public int? StatusCode { get; } = statusCode;
}
