using System.Text.Json.Serialization;

namespace VideoOptimiser.Cli.Features.Doctor;

internal sealed record DoctorJsonOutput(string ConfigurationPath, int ExitCode, DoctorJsonDiagnostic[] Diagnostics);
internal sealed record DoctorJsonDiagnostic(string Category, string Status, string Code, string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(DoctorJsonOutput))]
internal sealed partial class DoctorJsonContext : JsonSerializerContext;
