using System.Text.Json.Serialization;
using VideoOptimiser.Application.Features.Run;

namespace VideoOptimiser.Infrastructure.Features.Run;

/// <summary>
/// The event file's JSON, generated for Native AOT. One compact object per line, so no indenting; absent fields are left out
/// because an event sets only what its kind needs; enums are words so the file reads without the source beside it.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RunEvent))]
internal sealed partial class RunEventJsonContext : JsonSerializerContext;
