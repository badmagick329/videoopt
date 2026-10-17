using System.Text.Json;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Doctor;

namespace VideoOptimiser.Cli.Features.Doctor;

/// <summary><c>doctor</c>: one line per check, or the report as JSON for an agent. The exit code is the report's.</summary>
internal sealed class DoctorCommand(IDoctorService doctor)
{
    public async Task<int> RunAsync(LoadedConfiguration configuration, bool json, CancellationToken cancellationToken)
    {
        var report = await doctor.RunAsync(configuration, cancellationToken);
        if (json)
        {
            var output = new DoctorJsonOutput(
                configuration.Path,
                (int)report.ExitCode,
                report.Diagnostics.Select(diagnostic => new DoctorJsonDiagnostic(diagnostic.Category.ToString(), diagnostic.Status.ToString(), diagnostic.Code, diagnostic.Message)).ToArray());
            CliConsole.WriteLine(JsonSerializer.Serialize(output, DoctorJsonContext.Default.DoctorJsonOutput));
        }
        else
        {
            foreach (var diagnostic in report.Diagnostics)
            {
                CliConsole.WriteLine($"{diagnostic.Status,-7} {diagnostic.Category,-13} {diagnostic.Message}");
            }
        }

        return (int)report.ExitCode;
    }
}
