using System.Text.Json.Serialization;
using VideoOptimiser.Application.TemporaryOutputs;

namespace VideoOptimiser.Infrastructure.TemporaryOutputs;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(OutputManifest))]
internal sealed partial class VideoOptimiserJsonContext : JsonSerializerContext;
