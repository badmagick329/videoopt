using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Config;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Cli;
using VideoOptimiser.Cli.Features.Run;
using VideoOptimiser.Cli.Features.Tui;

internal enum CliCommandKind
{
    Invalid,
    Help,
    Version,
    ConfigInit,
    ConfigShow,
    ConfigValidate,
    Doctor,
    WorkerCreate,
    WorkerStatus,
    WorkerDelete,
    RemoteSetup,
    QueueAdd,
    QueueAddImmich,
    QueueAddFile,
    Run,
    QueueList,
    QueueHold,
    QueueRelease,
    QueueNext,
    QueueDrop,
    QueueReject,
    QueueDismiss,
    QueueRetry,
    Status,
    History,
    Validate,
    Finalize,
    Cleanup,
    ImmichRun,
    ImmichFinalize,
    Calibrate,
    Cost,
    StopRun,
    /// <summary>Hidden: the launcher that starts a background run (not in <see cref="CliCommand.Usage"/>; users start one from the TUI).</summary>
    BackgroundStart,
    /// <summary>Hidden: the detached process a background start launches.</summary>
    BackgroundRunner,
    /// <summary>Hidden: prints the TUI's screen once as plain text (<c>tui --snapshot</c>), so it can be checked from a shell.</summary>
    TuiSnapshot
}

/// <param name="Bare">Typed with no verb: the same card as <c>status</c>, but a missing configuration also says how to create one.</param>
/// <param name="StopNow"><c>run stop --now</c>.</param>
/// <param name="KeepWorker">Hidden <c>background start --keep-worker</c>: the worker's fate is keep instead of the default, delete.</param>
/// <param name="WorkerFate">Hidden <c>background runner --worker-fate</c>.</param>
/// <param name="ExistingWorker">Hidden <c>background runner --existing-worker</c>: the launcher chose the worker, so the runner never creates one.</param>
/// <param name="Size">Hidden <c>tui --snapshot --size</c>.</param>
internal sealed record CliCommand(CliCommandKind Kind, string? ConfigurationPath, string? ScanPath, bool Recursive, bool First, bool All, bool Json, string? Error, string? FilePath = null, bool Force = false, bool DryRun = false, string? JobId = null, bool FinalizeReady = false, ProcessingModeOverride? ModeOverride = null, int? Limit = null, QualityLevel? Quality = null, bool Bare = false, bool StopNow = false, bool KeepWorker = false, WorkerFateChoice? WorkerFate = null, FrameSize? Size = null, bool ExistingWorker = false)
{
    public const string Usage = """
Usage:
  video-optimiser [--config <path>]
  video-optimiser config init [--config <path>] [--quality archive|standard|compact]
  video-optimiser version
  video-optimiser config show [--config <path>]
  video-optimiser config validate [--config <path>]
  video-optimiser doctor [--config <path>] [--json]
  video-optimiser worker create [--config <path>]
  video-optimiser worker status [--config <path>]
  video-optimiser worker delete [--config <path>]
  video-optimiser remote setup [--config <path>]
  video-optimiser queue add [--first] [--config <path>]
  video-optimiser queue add immich [--first | --limit <n>] [--config <path>]
  video-optimiser queue add <file> [--force] [--config <path>]
  video-optimiser run [--config <path>] [--local | --remote]
  video-optimiser run stop [--now] [--config <path>]
  video-optimiser queue list [--config <path>] [--json]
  video-optimiser queue hold <job-id> [--config <path>]
  video-optimiser queue release <job-id> [--config <path>]
  video-optimiser queue next <job-id> [--config <path>]
  video-optimiser queue drop <job-id> [--config <path>]
  video-optimiser queue drop --all [--config <path>]
  video-optimiser queue reject <job-id> [--config <path>]
  video-optimiser queue dismiss <job-id> [--config <path>]
  video-optimiser queue dismiss --all [--config <path>]
  video-optimiser queue retry <job-id> [--config <path>]
  video-optimiser status [--config <path>] [--json]
  video-optimiser history [--config <path>] [--json]
  video-optimiser calibrate [--config <path>] [--json]
  video-optimiser cost [--config <path>] [--json]
  video-optimiser validate <job-id> [--config <path>]
  video-optimiser finalize <job-id> [--config <path>]
  video-optimiser finalize --ready [--config <path>]
  video-optimiser cleanup [--dry-run] [--config <path>]
  video-optimiser immich run [--first | --limit <n>] [--config <path>] [--local | --remote]
  video-optimiser immich finalize [--config <path>]
""";

    public bool IsValid => Kind != CliCommandKind.Invalid;
    public bool ShowHelp => Kind == CliCommandKind.Help;

    /// <summary>A command to suggest, such as <c>queue release &lt;id&gt;</c>, with <c>--config</c> only when this one was given it.</summary>
    public string Hint(string verb) => CommandHints.Format(verb, ConfigurationPath);

    public static CliCommand Parse(string[] args)
    {
        var remaining = new List<string>();
        string? configurationPath = null;
        var force = false;
        var dryRun = false;
        var first = false;
        int? limit = null;
        QualityLevel? quality = null;
        var all = false;
        var json = false;
        var finalizeReady = false;
        var stopNow = false;
        var keepWorker = false;
        WorkerFateChoice? workerFate = null;
        var existingWorker = false;
        var snapshot = false;
        FrameSize? size = null;
        var modeOverrides = new List<ProcessingModeOverride>();

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config" when index + 1 < args.Length:
                    configurationPath = args[++index];
                    break;
                case "--config":
                    return Invalid("--config requires a path.");
                case "--json":
                    json = true;
                    break;
                case "--all":
                    all = true;
                    break;
                case "--first":
                    first = true;
                    break;
                case "--limit" when index + 1 < args.Length && int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0:
                    limit = parsed;
                    index++;
                    break;
                case "--limit":
                    return Invalid("--limit requires a whole number of 1 or more.");
                case "--quality" when index + 1 < args.Length && QualityLevels.TryParse(args[index + 1], out var level):
                    quality = level;
                    index++;
                    break;
                case "--quality":
                    return Invalid("--quality requires archive, standard or compact.");
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--ready":
                    finalizeReady = true;
                    break;
                case "--now":
                    stopNow = true;
                    break;
                case "--keep-worker":
                    keepWorker = true;
                    break;
                case "--worker-fate" when index + 1 < args.Length && TryParseWorkerFate(args[index + 1], out var parsedFate):
                    workerFate = parsedFate;
                    index++;
                    break;
                case "--worker-fate":
                    return Invalid("--worker-fate requires delete or keep.");
                case BackgroundRunnerArguments.ExistingWorkerFlag:
                    existingWorker = true;
                    break;
                case "--snapshot":
                    snapshot = true;
                    break;
                case "--size" when index + 1 < args.Length && FrameSize.TryParse(args[index + 1], out var parsedSize):
                    size = parsedSize;
                    index++;
                    break;
                case "--size":
                    return Invalid("--size requires WIDTHxHEIGHT, such as 140x40.");
                case "--local":
                    modeOverrides.Add(ProcessingModeOverride.Local);
                    break;
                case "--remote":
                    modeOverrides.Add(ProcessingModeOverride.Remote);
                    break;
                case "--crf":
                    return Invalid("--crf is no longer supported: run picks the CRF itself.");
                case "--help" or "-h" or "help":
                    return new CliCommand(CliCommandKind.Help, null, null, false, false, false, false, null);
                case "--version":
                    return new CliCommand(CliCommandKind.Version, null, null, false, false, false, false, null);
                default:
                    remaining.Add(args[index]);
                    break;
            }
        }

        var kind = remaining switch
        {
            [] => CliCommandKind.Status,
            ["config", "init"] => CliCommandKind.ConfigInit,
            ["config", "show"] => CliCommandKind.ConfigShow,
            ["config", "validate"] => CliCommandKind.ConfigValidate,
            ["version"] => CliCommandKind.Version,
            ["doctor"] => CliCommandKind.Doctor,
            ["worker", "create"] => CliCommandKind.WorkerCreate,
            ["worker", "status"] => CliCommandKind.WorkerStatus,
            ["worker", "delete"] => CliCommandKind.WorkerDelete,
            ["remote", "setup"] => CliCommandKind.RemoteSetup,
            ["queue", "add"] => CliCommandKind.QueueAdd,
            // The word itself selects it; a file named "immich" is given as ".\immich".
            ["queue", "add", "immich"] => CliCommandKind.QueueAddImmich,
            ["queue", "add", _] => CliCommandKind.QueueAddFile,
            ["run"] => CliCommandKind.Run,
            ["run", "stop"] => CliCommandKind.StopRun,
            ["background", "start"] => CliCommandKind.BackgroundStart,
            ["background", "runner"] => CliCommandKind.BackgroundRunner,
            ["tui"] => CliCommandKind.TuiSnapshot,
            ["queue", "list"] => CliCommandKind.QueueList,
            ["queue", "hold", _] => CliCommandKind.QueueHold,
            ["queue", "release", _] => CliCommandKind.QueueRelease,
            ["queue", "next", _] => CliCommandKind.QueueNext,
            ["queue", "drop"] or ["queue", "drop", _] => CliCommandKind.QueueDrop,
            ["queue", "reject", _] => CliCommandKind.QueueReject,
            ["queue", "dismiss"] or ["queue", "dismiss", _] => CliCommandKind.QueueDismiss,
            ["queue", "retry", _] => CliCommandKind.QueueRetry,
            ["status"] => CliCommandKind.Status,
            ["history"] => CliCommandKind.History,
            ["calibrate"] => CliCommandKind.Calibrate,
            ["cost"] => CliCommandKind.Cost,
            ["validate", _] => CliCommandKind.Validate,
            ["finalize"] or ["finalize", _] => CliCommandKind.Finalize,
            ["cleanup"] => CliCommandKind.Cleanup,
            ["immich", "run"] => CliCommandKind.ImmichRun,
            ["immich", "finalize"] => CliCommandKind.ImmichFinalize,
            _ => CliCommandKind.Invalid
        };

        if (kind == CliCommandKind.Invalid)
        {
            // The names phase A replaced, with no aliases: each says what took its place.
            return Invalid(remaining switch
            {
                ["queue", "cancel", ..] => "'queue cancel' is now 'queue drop'.",
                ["queue", "run", ..] => "'queue run' is now 'run'.",
                ["queue", "discover", ..] => "'queue discover' is now 'queue add'.",
                ["process", ..] => "'process' was removed: use 'queue add <file>', then 'run'.",
                _ => "Unknown or incomplete command."
            });
        }

        if (json && kind is not (CliCommandKind.Doctor or CliCommandKind.QueueList or CliCommandKind.Status or CliCommandKind.History or CliCommandKind.Calibrate or CliCommandKind.Cost))
        {
            return Invalid("--json is only supported by doctor, queue list, status, history, calibrate and cost.");
        }

        if (first && kind is not (CliCommandKind.QueueAdd or CliCommandKind.QueueAddImmich or CliCommandKind.ImmichRun)) return Invalid("--first is only supported by queue add, queue add immich and immich run.");
        if (limit is not null && kind is not (CliCommandKind.QueueAddImmich or CliCommandKind.ImmichRun)) return Invalid("--limit is only supported by queue add immich and immich run.");
        if (quality is not null && kind != CliCommandKind.ConfigInit) return Invalid("--quality is only supported by config init.");
        if (first && limit is not null) return Invalid("--first and --limit cannot be used together.");
        // --first is --limit 1 for the Immich commands; queue add keeps its own --first.
        if (first && kind is CliCommandKind.QueueAddImmich or CliCommandKind.ImmichRun) limit = 1;
        if (all && kind is not (CliCommandKind.QueueDrop or CliCommandKind.QueueDismiss)) return Invalid("--all is only supported by queue drop and queue dismiss.");

        if (force && kind != CliCommandKind.QueueAddFile)
        {
            return Invalid("--force is only supported by queue add <file>.");
        }

        if (dryRun && kind != CliCommandKind.Cleanup) return Invalid("--dry-run is only supported by cleanup.");
        if (finalizeReady && kind != CliCommandKind.Finalize) return Invalid("--ready is only supported by finalize.");
        if (finalizeReady && remaining.Count != 1) return Invalid("finalize --ready does not take a job ID.");
        if (kind == CliCommandKind.QueueDrop && all == (remaining.Count == 3)) return Invalid("queue drop requires a job ID or --all.");
        if (kind == CliCommandKind.QueueDismiss && all == (remaining.Count == 3)) return Invalid("queue dismiss requires a job ID or --all.");
        if (kind == CliCommandKind.QueueRetry && remaining.Count != 3) return Invalid("queue retry requires a job ID.");
        if (stopNow && kind != CliCommandKind.StopRun) return Invalid("--now is only supported by run stop.");
        if (keepWorker && kind != CliCommandKind.BackgroundStart) return Invalid("--keep-worker is only supported by background start.");
        if (workerFate is not null && kind != CliCommandKind.BackgroundRunner) return Invalid("--worker-fate is only supported by background runner.");
        if (workerFate is null && kind == CliCommandKind.BackgroundRunner) return Invalid("background runner requires --worker-fate delete or keep.");
        if (existingWorker && kind != CliCommandKind.BackgroundRunner) return Invalid("--existing-worker is only supported by background runner.");
        if (snapshot && kind != CliCommandKind.TuiSnapshot) return Invalid("--snapshot is only supported by tui.");
        if (size is not null && kind != CliCommandKind.TuiSnapshot) return Invalid("--size is only supported by tui.");
        if (!snapshot && kind == CliCommandKind.TuiSnapshot) return Invalid("tui requires --snapshot.");
        if (modeOverrides.Distinct().Count() > 1) return Invalid("--local and --remote cannot be used together.");
        var modeOverride = modeOverrides.FirstOrDefault();
        if (modeOverride is not null && kind is not (CliCommandKind.Run or CliCommandKind.ImmichRun or CliCommandKind.BackgroundStart or CliCommandKind.BackgroundRunner))
        {
            return Invalid($"{modeOverride.Flag} is only supported by run and immich run.");
        }

        return new CliCommand(kind, configurationPath, null, false, first, all, json, null, kind == CliCommandKind.QueueAddFile ? remaining[2] : null, force, dryRun, (kind is CliCommandKind.Validate or CliCommandKind.Finalize or CliCommandKind.QueueHold or CliCommandKind.QueueRelease or CliCommandKind.QueueNext or CliCommandKind.QueueDrop or CliCommandKind.QueueReject or CliCommandKind.QueueDismiss or CliCommandKind.QueueRetry) && remaining.Count > 1 ? remaining[^1] : null, finalizeReady, modeOverride, limit, quality, Bare: remaining.Count == 0, StopNow: stopNow, KeepWorker: keepWorker, WorkerFate: workerFate, Size: size, ExistingWorker: existingWorker);
    }

    private static bool TryParseWorkerFate(string text, out WorkerFateChoice fate)
    {
        (var parsed, fate) = text.ToLowerInvariant() switch
        {
            "delete" => (true, WorkerFateChoice.Delete),
            "keep" => (true, WorkerFateChoice.Keep),
            _ => (false, default)
        };
        return parsed;
    }

    private static CliCommand Invalid(string message) => new(CliCommandKind.Invalid, null, null, false, false, false, false, message);
}
