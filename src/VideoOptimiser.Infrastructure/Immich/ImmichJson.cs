using System.Text.Json.Serialization;

namespace VideoOptimiser.Infrastructure.Immich;

// Wire shapes of the Immich v3.2.2 API (open-api/immich-openapi-specs.json), holding only the fields this app uses.
// Null properties are omitted, except where Immich reads an explicit null as "is null".

internal sealed class ServerVersionDto
{
    public int Major { get; init; }
    public int Minor { get; init; }
    public int Patch { get; init; }
}

internal sealed class ServerFeaturesDto
{
    public bool Trash { get; init; }
}

internal sealed class ServerConfigDto
{
    public int TrashDays { get; init; }
}

internal sealed class UserDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

internal sealed class MetadataSearchRequest
{
    public required SearchFilterDto Filter { get; init; }
    public int Size { get; init; }
    public string? Cursor { get; init; }
    public bool WithExif { get; init; }
    public SearchOrderDto? OrderBy { get; init; }
}

// v3.2.0 deprecated the flat MetadataSearchDto fields (type, takenBefore, visibility, libraryId, page) for this object.
internal sealed class SearchFilterDto
{
    public ValueFilter? Type { get; init; }
    public DateFilter? TakenAt { get; init; }
    public ListFilter? Visibility { get; init; }
    public NullFilter? LibraryId { get; init; }
    // The filter search returns trashed assets unless this is set (seen on v3.2.2).
    public NullFilter? TrashedAt { get; init; }
    public BoolFilter? IsFavorite { get; init; }
}

internal sealed class ValueFilter
{
    public required string Eq { get; init; }
}

internal sealed class ListFilter
{
    public required IReadOnlyList<string> In { get; init; }
}

internal sealed class DateFilter
{
    public DateTimeOffset? Lt { get; init; }
}

internal sealed class BoolFilter
{
    public bool Eq { get; init; }
}

/// <summary>Matches a null field: Immich needs <c>"eq": null</c> written out.</summary>
internal sealed class NullFilter
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Eq { get; init; }
}

internal sealed class SearchOrderDto
{
    public required string Field { get; init; }
    public required string Direction { get; init; }
}

internal sealed class SearchResponseDto
{
    public SearchAssetsDto Assets { get; init; } = new();
}

internal sealed class SearchAssetsDto
{
    public List<AssetDto> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}

internal sealed class AssetDto
{
    public string Id { get; init; } = "";
    public string Checksum { get; init; } = "";
    public string OriginalFileName { get; init; } = "";
    public string Type { get; init; } = "";
    public string Visibility { get; init; } = "";
    public bool IsFavorite { get; init; }
    public bool IsTrashed { get; init; }
    public string? LibraryId { get; init; }
    public string? LivePhotoVideoId { get; init; }
    public DateTimeOffset FileCreatedAt { get; init; }
    public DateTimeOffset FileModifiedAt { get; init; }
    public DateTimeOffset LocalDateTime { get; init; }
    // Milliseconds since v3; older servers sent "H:MM:SS.fff", which this client does not support.
    public long? Duration { get; init; }
    public ExifDto? ExifInfo { get; init; }
    public List<TagDto>? Tags { get; init; }
}

internal sealed class ExifDto
{
    public long? FileSizeInByte { get; init; }
    public DateTimeOffset? DateTimeOriginal { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? Description { get; init; }
    public int? Rating { get; init; }
    public string? Make { get; init; }
    public string? Model { get; init; }
}

internal sealed class TagDto
{
    public string Id { get; init; } = "";
}

internal sealed class UploadResponseDto
{
    public string Id { get; init; } = "";
    public string Status { get; init; } = "";
}

internal sealed class UpdateAssetRequest
{
    public DateTimeOffset? DateTimeOriginal { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? Description { get; init; }
    public int? Rating { get; init; }
}

internal sealed class AssetCopyRequest
{
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public bool Albums { get; init; }
    public bool Favorite { get; init; }
    public bool SharedLinks { get; init; }
    public bool Stack { get; init; }
    public bool Sidecar { get; init; }
}

internal sealed class TagAssetsRequest
{
    public required IReadOnlyList<string> AssetIds { get; init; }
    public required IReadOnlyList<string> TagIds { get; init; }
}

internal sealed class AssetDeleteRequest
{
    public required IReadOnlyList<string> Ids { get; init; }
    public bool Force { get; init; }
}

internal sealed class UploadCheckRequest
{
    public required IReadOnlyList<UploadCheckItem> Assets { get; init; }
}

internal sealed class UploadCheckItem
{
    public required string Id { get; init; }
    public required string Checksum { get; init; }
}

internal sealed class UploadCheckResponse
{
    public List<UploadCheckResult> Results { get; init; } = [];
}

internal sealed class UploadCheckResult
{
    public string Id { get; init; } = "";
    public string Action { get; init; } = "";
    public string? Reason { get; init; }
    public string? AssetId { get; init; }
    public bool IsTrashed { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ServerVersionDto))]
[JsonSerializable(typeof(ServerFeaturesDto))]
[JsonSerializable(typeof(ServerConfigDto))]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(MetadataSearchRequest))]
[JsonSerializable(typeof(SearchResponseDto))]
[JsonSerializable(typeof(AssetDto))]
[JsonSerializable(typeof(UploadResponseDto))]
[JsonSerializable(typeof(UpdateAssetRequest))]
[JsonSerializable(typeof(AssetCopyRequest))]
[JsonSerializable(typeof(TagAssetsRequest))]
[JsonSerializable(typeof(AssetDeleteRequest))]
[JsonSerializable(typeof(UploadCheckRequest))]
[JsonSerializable(typeof(UploadCheckResponse))]
internal sealed partial class ImmichJsonContext : JsonSerializerContext;
