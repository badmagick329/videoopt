using System.Text.Json;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Calibrate;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Calibrate;

/// <summary>
/// <c>calibrate</c>: what the rules admit and keep out, and suggestions. Scan issues end in <see cref="ExitCode.PartialSuccess"/>,
/// as for <c>queue add</c>, because the report is missing the videos they left out.
/// </summary>
internal sealed class CalibrateCommand(ICalibrationService calibration, ISettingsValidator validator)
{
    public async Task<int> RunAsync(LoadedConfiguration configuration, bool json, CancellationToken cancellationToken)
    {
        if (ReportInvalidSettings(validator, configuration)) return (int)ExitCode.InvalidConfiguration;
        // With --json, stdout carries the JSON alone; an agent has no use for progress lines.
        var scanProgress = json ? null : new QueueDiscoverConsole(Console.Out);
        var immichProgress = json ? null : new ImmichSearchConsole(Console.Out);
        CalibrationRun run;
        try
        {
            run = await calibration.RunAsync(configuration.Settings, scanProgress, immichProgress, cancellationToken);
        }
        catch (DatabaseOutdatedException exception)
        {
            WriteError($"{Path.GetFileName(exception.DatabasePath)} is from an older version of video-optimiser. Run 'video-optimiser status' once to upgrade it, then run calibrate again.");
            return (int)ExitCode.GeneralFailure;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(CalibrateConsole.ToJson(run.Report), CalibrateJsonContext.Default.CalibrationJsonOutput));
        }
        else
        {
            if (scanProgress!.WroteLines || immichProgress!.WroteLines) Console.WriteLine();
            CalibrateConsole.Write(Console.Out, run.Report);
        }

        if (run.ImmichFailure is { } failure)
        {
            // After the report, so the folder half is on screen first; the exit code is the one any Immich failure ends a command with.
            WriteError($"Immich couldn't be searched, so its videos are not in this report: {failure.Message.TrimEnd('.')}.");
            return ImmichFailureExitCode(failure);
        }

        return run.ScanIssues == 0 ? (int)ExitCode.Success : (int)ExitCode.PartialSuccess;
    }
}
