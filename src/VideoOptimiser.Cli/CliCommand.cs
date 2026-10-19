using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Config;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Cli;
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

    /// <summary>
    /// Every refusal happens here, before the host is built, so a mistyped command never takes a lock or touches the database.
    /// </summary>
    /// <remarks>
    /// Any token that starts with <c>--</c> is an option: a known one from <see cref="CliOptions.All"/>, or an error. A value
    /// that starts with a dash (<c>--limit -1</c>, <c>--config --odd.yaml</c>) is read by its option first, so it never gets
    /// that far, and a file named like an option is given as <c>.\--name.mp4</c>, as a file named <c>immich</c> is.
    /// </remarks>
    public static CliCommand Parse(string[] args)
    {
        var words = new List<string>();
        var values = new CliOptionValues();
        var given = new List<CliOption>();
        string? unknown = null;

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            switch (token)
            {
                case "--help" or "-h":
                    return new CliCommand(CliCommandKind.Help, null, null, false, false, false, false, null);
                case "--version":
                    return new CliCommand(CliCommandKind.Version, null, null, false, false, false, false, null);
                case "--crf":
                    return Invalid("--crf is no longer supported: run picks the CRF itself.");
            }

            if (CliOptions.Find(token) is { } option)
            {
                string? value = null;
                if (option.TakesValue)
                {
                    if (index + 1 >= args.Length) return Invalid(option.ValueError!);
                    value = args[++index];
                }
                if (!option.Set(values, value)) return Invalid(option.ValueError!);
                given.Add(option);
            }
            // Kept for after the loop: a --help later on the line still wins, and the suggestion depends on the command.
            else if (token.StartsWith("--", StringComparison.Ordinal)) unknown ??= token;
            else words.Add(token);
        }

        // Only as the first word, as the help documents it; `queue add help` is a file named help.
        if (words is ["help", ..]) return new CliCommand(CliCommandKind.Help, null, null, false, false, false, false, null);

        var (kind, jobId, filePath) = Match(words);
        if (unknown is not null)
        {
            var suggestion = CliOptions.Suggest(unknown, kind);
            return Invalid(suggestion is null ? $"Unknown option '{unknown}'." : $"Unknown option '{unknown}'. Did you mean '{suggestion}'?");
        }

        if (kind == CliCommandKind.Invalid)
        {
            // The names phase A replaced, with no aliases: each says what took its place.
            return Invalid(words switch
            {
                ["queue", "cancel", ..] => "'queue cancel' is now 'queue drop'.",
                ["queue", "run", ..] => "'queue run' is now 'run'.",
                ["queue", "discover", ..] => "'queue discover' is now 'queue add'.",
                ["process", ..] => "'process' was removed: use 'queue add <file>', then 'run'.",
                _ => "Unknown or incomplete command."
            });
        }

        if (given.FirstOrDefault(option => !option.AcceptedBy(kind)) is { } unsupported) return Invalid(CliOptions.UnsupportedError(unsupported));

        if (values.First && values.Limit is not null) return Invalid("--first and --limit cannot be used together.");
        if (values.Modes.Distinct().Count() > 1) return Invalid("--local and --remote cannot be used together.");
        if (kind == CliCommandKind.Finalize && values.Ready && jobId is not null) return Invalid("finalize --ready does not take a job ID.");
        if (kind == CliCommandKind.Finalize && !values.Ready && jobId is null) return Invalid("finalize requires a job ID or --ready.");
        if (kind == CliCommandKind.QueueDrop && values.All == (jobId is not null)) return Invalid("queue drop requires a job ID or --all.");
        if (kind == CliCommandKind.QueueDismiss && values.All == (jobId is not null)) return Invalid("queue dismiss requires a job ID or --all.");
        if (kind == CliCommandKind.BackgroundRunner && values.WorkerFate is null) return Invalid("background runner requires --worker-fate delete or keep.");
        if (kind == CliCommandKind.TuiSnapshot && !values.Snapshot) return Invalid("tui requires --snapshot.");

        // --first is --limit 1 for the Immich commands; queue add keeps its own --first.
        var limit = values.First && kind is CliCommandKind.QueueAddImmich or CliCommandKind.ImmichRun ? 1 : values.Limit;
        return new CliCommand(kind, values.ConfigurationPath, null, false, values.First, values.All, values.Json, null, filePath, values.Force, values.DryRun, jobId, values.Ready, values.Modes.FirstOrDefault(), limit, values.Quality, Bare: words.Count == 0, StopNow: values.StopNow, KeepWorker: values.KeepWorker, WorkerFate: values.WorkerFate, Size: values.Size, ExistingWorker: values.ExistingWorker);
    }

    /// <summary>
    /// The command the words name, with the job ID or file taken from its place in that command's pattern, so a word that only
    /// selects the command (<c>drop</c> in <c>queue drop --all</c>) is never read as one.
    /// </summary>
    private static (CliCommandKind Kind, string? JobId, string? FilePath) Match(List<string> words) => words switch
    {
        [] => (CliCommandKind.Status, null, null),
        ["config", "init"] => (CliCommandKind.ConfigInit, null, null),
        ["config", "show"] => (CliCommandKind.ConfigShow, null, null),
        ["config", "validate"] => (CliCommandKind.ConfigValidate, null, null),
        ["version"] => (CliCommandKind.Version, null, null),
        ["doctor"] => (CliCommandKind.Doctor, null, null),
        ["worker", "create"] => (CliCommandKind.WorkerCreate, null, null),
        ["worker", "status"] => (CliCommandKind.WorkerStatus, null, null),
        ["worker", "delete"] => (CliCommandKind.WorkerDelete, null, null),
        ["remote", "setup"] => (CliCommandKind.RemoteSetup, null, null),
        ["queue", "add"] => (CliCommandKind.QueueAdd, null, null),
        // The word itself selects it; a file named "immich" is given as ".\immich".
        ["queue", "add", "immich"] => (CliCommandKind.QueueAddImmich, null, null),
        ["queue", "add", var file] => (CliCommandKind.QueueAddFile, null, file),
        ["run"] => (CliCommandKind.Run, null, null),
        ["run", "stop"] => (CliCommandKind.StopRun, null, null),
        ["background", "start"] => (CliCommandKind.BackgroundStart, null, null),
        ["background", "runner"] => (CliCommandKind.BackgroundRunner, null, null),
        ["tui"] => (CliCommandKind.TuiSnapshot, null, null),
        ["queue", "list"] => (CliCommandKind.QueueList, null, null),
        ["queue", "hold", var id] => (CliCommandKind.QueueHold, id, null),
        ["queue", "release", var id] => (CliCommandKind.QueueRelease, id, null),
        ["queue", "next", var id] => (CliCommandKind.QueueNext, id, null),
        ["queue", "drop"] => (CliCommandKind.QueueDrop, null, null),
        ["queue", "drop", var id] => (CliCommandKind.QueueDrop, id, null),
        ["queue", "reject", var id] => (CliCommandKind.QueueReject, id, null),
        ["queue", "dismiss"] => (CliCommandKind.QueueDismiss, null, null),
        ["queue", "dismiss", var id] => (CliCommandKind.QueueDismiss, id, null),
        ["queue", "retry", var id] => (CliCommandKind.QueueRetry, id, null),
        ["status"] => (CliCommandKind.Status, null, null),
        ["history"] => (CliCommandKind.History, null, null),
        ["calibrate"] => (CliCommandKind.Calibrate, null, null),
        ["cost"] => (CliCommandKind.Cost, null, null),
        ["validate", var id] => (CliCommandKind.Validate, id, null),
        ["finalize"] => (CliCommandKind.Finalize, null, null),
        ["finalize", var id] => (CliCommandKind.Finalize, id, null),
        ["cleanup"] => (CliCommandKind.Cleanup, null, null),
        ["immich", "run"] => (CliCommandKind.ImmichRun, null, null),
        ["immich", "finalize"] => (CliCommandKind.ImmichFinalize, null, null),
        _ => (CliCommandKind.Invalid, null, null)
    };

    private static CliCommand Invalid(string message) => new(CliCommandKind.Invalid, null, null, false, false, false, false, message);
}
