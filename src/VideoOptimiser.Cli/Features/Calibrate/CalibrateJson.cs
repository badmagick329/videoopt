using System.Text.Json.Serialization;

namespace VideoOptimiser.Cli.Features.Calibrate;

internal sealed record CalibrationJsonOutput(double MinimumVmaf, CalibrationRuleJson[] Rules, CalibrationGroupJson[] KeptOut, CalibrationUnknownJson[] CodecUnknown, CalibrationTotalJson AlreadyAv1, CalibrationLeftOutJson HistoryLeftOut, CalibrationSuggestionJson[] Suggestions);
internal sealed record CalibrationRuleJson(int Number, string[] Codecs, string Resolution, string MinimumVideoBitrate, string MinimumFileSize, int Videos, long Bytes, CalibrationHistoryJson? History);
// A null resolution is unknown; null Mbps bounds are an unknown bitrate, or a null maximum alone the open top band.
internal sealed record CalibrationGroupJson(string Codec, string? Resolution, int? MinimumMbps, int? MaximumMbps, int Videos, long Bytes, CalibrationHistoryJson? History);
internal sealed record CalibrationUnknownJson(string? Resolution, int? MinimumMbps, int? MaximumMbps, int Videos, long Bytes);
internal sealed record CalibrationHistoryJson(int Converted, CalibrationRangeJson? ConvertedBitrateMbps, CalibrationRangeJson? SavedPercent, int NotWorthIt, CalibrationRangeJson? NotWorthItBitrateMbps);
internal sealed record CalibrationRangeJson(int Min, int Max);
internal sealed record CalibrationTotalJson(int Videos, long Bytes);
internal sealed record CalibrationLeftOutJson(int OtherSettings, int NoVideoFacts);

// "kind" names the suggestion; each kind carries only its own fields, then the sentence the console printed.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RaiseMinimumBitrateJson), "raiseMinimumBitrate")]
[JsonDerivedType(typeof(AddRuleJson), "addRule")]
[JsonDerivedType(typeof(TestOneJson), "testOne")]
internal abstract record CalibrationSuggestionJson([property: JsonPropertyOrder(1)] string Text);
internal sealed record RaiseMinimumBitrateJson(int Rule, string MinimumVideoBitrate, string Text) : CalibrationSuggestionJson(Text);
internal sealed record AddRuleJson(string Codec, string Resolution, string MinimumVideoBitrate, string Text) : CalibrationSuggestionJson(Text);
internal sealed record TestOneJson(string? Codec, string? Resolution, int? MinimumMbps, int? MaximumMbps, string? Path, string? ImmichAssetId, string? Command, string Text) : CalibrationSuggestionJson(Text);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CalibrationJsonOutput))]
internal sealed partial class CalibrateJsonContext : JsonSerializerContext;
