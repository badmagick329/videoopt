using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Config;

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
    QueueAddFile,
    Run,
    QueueList,
    QueueHold,
    QueueRelease,
    QueueNext,
    QueueDrop,
    QueueRetry,
    Status,
    History,
    Validate,
    Finalize,
    Cleanup,
    ImmichRun,
    ImmichFinalize,
    Calibrate
}

internal sealed record CliCommand(CliCommandKind Kind, string? ConfigurationPath, string? ScanPath, bool Recursive, bool First, bool All, bool Json, string? Error, string? FilePath = null, bool Force = false, bool DryRun = false, string? JobId = null, bool FinalizeReady = false, ProcessingModeOverride? ModeOverride = null, int? Limit = null, QualityLevel? Quality = null)
{
    public const string Usage = """
Usage:
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
  video-optimiser queue add <file> [--force] [--config <path>]
  video-optimiser run [--config <path>] [--local | --remote]
  video-optimiser queue list [--config <path>] [--json]
  video-optimiser queue hold <job-id> [--config <path>]
  video-optimiser queue release <job-id> [--config <path>]
  video-optimiser queue next <job-id> [--config <path>]
  video-optimiser queue drop <job-id> [--config <path>]
  video-optimiser queue drop --all [--config <path>]
  video-optimiser queue retry <job-id> [--config <path>]
  video-optimiser status [--config <path>] [--json]
  video-optimiser history [--config <path>] [--json]
  video-optimiser calibrate [--config <path>] [--json]
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
    public string Hint(string verb) => $"video-optimiser {verb}{(ConfigurationPath is null ? string.Empty : $" --config \"{ConfigurationPath}\"")}";

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
            ["queue", "add", _] => CliCommandKind.QueueAddFile,
            ["run"] => CliCommandKind.Run,
            ["queue", "list"] => CliCommandKind.QueueList,
            ["queue", "hold", _] => CliCommandKind.QueueHold,
            ["queue", "release", _] => CliCommandKind.QueueRelease,
            ["queue", "next", _] => CliCommandKind.QueueNext,
            ["queue", "drop"] or ["queue", "drop", _] => CliCommandKind.QueueDrop,
            ["queue", "retry", _] => CliCommandKind.QueueRetry,
            ["status"] => CliCommandKind.Status,
            ["history"] => CliCommandKind.History,
            ["calibrate"] => CliCommandKind.Calibrate,
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

        if (json && kind is not (CliCommandKind.Doctor or CliCommandKind.QueueList or CliCommandKind.Status or CliCommandKind.History or CliCommandKind.Calibrate))
        {
            return Invalid("--json is only supported by doctor, queue list, status, history and calibrate.");
        }

        if (first && kind is not (CliCommandKind.QueueAdd or CliCommandKind.ImmichRun)) return Invalid("--first is only supported by queue add and immich run.");
        if (limit is not null && kind != CliCommandKind.ImmichRun) return Invalid("--limit is only supported by immich run.");
        if (quality is not null && kind != CliCommandKind.ConfigInit) return Invalid("--quality is only supported by config init.");
        if (first && limit is not null) return Invalid("--first and --limit cannot be used together.");
        // --first is --limit 1 for immich run; queue add keeps its own --first.
        if (first && kind == CliCommandKind.ImmichRun) limit = 1;
        if (all && kind != CliCommandKind.QueueDrop) return Invalid("--all is only supported by queue drop.");

        if (force && kind != CliCommandKind.QueueAddFile)
        {
            return Invalid("--force is only supported by queue add <file>.");
        }

        if (dryRun && kind != CliCommandKind.Cleanup) return Invalid("--dry-run is only supported by cleanup.");
        if (finalizeReady && kind != CliCommandKind.Finalize) return Invalid("--ready is only supported by finalize.");
        if (finalizeReady && remaining.Count != 1) return Invalid("finalize --ready does not take a job ID.");
        if (kind == CliCommandKind.QueueDrop && all == (remaining.Count == 3)) return Invalid("queue drop requires a job ID or --all.");
        if (kind == CliCommandKind.QueueRetry && remaining.Count != 3) return Invalid("queue retry requires a job ID.");
        if (modeOverrides.Distinct().Count() > 1) return Invalid("--local and --remote cannot be used together.");
        var modeOverride = modeOverrides.FirstOrDefault();
        if (modeOverride is not null && kind is not (CliCommandKind.Run or CliCommandKind.ImmichRun))
        {
            return Invalid($"{modeOverride.Flag} is only supported by run and immich run.");
        }

        return new CliCommand(kind, configurationPath, null, false, first, all, json, null, kind == CliCommandKind.QueueAddFile ? remaining[2] : null, force, dryRun, (kind is CliCommandKind.Validate or CliCommandKind.Finalize or CliCommandKind.QueueHold or CliCommandKind.QueueRelease or CliCommandKind.QueueNext or CliCommandKind.QueueDrop or CliCommandKind.QueueRetry) && remaining.Count > 1 ? remaining[^1] : null, finalizeReady, modeOverride, limit, quality);
    }

    private static CliCommand Invalid(string message) => new(CliCommandKind.Invalid, null, null, false, false, false, false, message);
}
