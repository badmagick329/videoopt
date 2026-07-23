using System.Text.Json.Serialization;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.Infrastructure.Processing;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(OutputManifest))]
internal sealed partial class VideoOptimiserJsonContext : JsonSerializerContext;
