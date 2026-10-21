using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Config;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Cli.Features.Run;
using VideoOptimiser.Cli.Features.Tui;

/// <summary>What the options on one command line set, filled in as <see cref="CliCommand.Parse"/> reads them.</summary>
internal sealed class CliOptionValues
{
    public string? ConfigurationPath;
    public bool Json, All, First, DryRun, Force, Ready, StopNow, UndoStop, KeepWorker, ExistingWorker, Snapshot;
    public int? Limit;
    public QualityLevel? Quality;
    public WorkerFateChoice? WorkerFate;
    public FrameSize? Size;
    public IReadOnlyList<ConsoleKeyInfo>? SnapshotKeys;
    public readonly List<ProcessingModeOverride> Modes = [];
}

/// <summary>
/// One option the parser knows. <paramref name="Kinds"/> is every command that accepts it (null: all of them), and the
/// "only supported by" error is generated from it, so a new command or option is one row here rather than another check.
/// </summary>
/// <param name="Set">For an option that takes a value: stores it and says whether it was valid. For a switch the value is null.</param>
/// <param name="ValueError">Non-null exactly when the option takes a value; printed when the value is missing or invalid.</param>
internal sealed record CliOption(string Name, CliCommandKind[]? Kinds, Func<CliOptionValues, string?, bool> Set, string? ValueError = null)
{
    public bool TakesValue => ValueError is not null;
    public bool AcceptedBy(CliCommandKind kind) => Kinds is null || Kinds.Contains(kind);

    /// <summary>Only the hidden commands accept it, so a typo elsewhere never suggests it.</summary>
    public bool Hidden => Kinds is not null && Kinds.All(CliOptions.IsHidden);
}

internal static class CliOptions
{
    /// <summary>Each option once, in the order the "only supported by" lists name their commands.</summary>
    public static readonly CliOption[] All =
    [
        Value("--config", null, "--config requires a path.", (values, text) => { values.ConfigurationPath = text; return true; }),
        Switch("--json", [CliCommandKind.Doctor, CliCommandKind.QueueList, CliCommandKind.Status, CliCommandKind.History, CliCommandKind.Calibrate, CliCommandKind.Cost], values => values.Json = true),
        Switch("--all", [CliCommandKind.QueueDrop, CliCommandKind.QueueDismiss], values => values.All = true),
        Switch("--first", [CliCommandKind.QueueAdd, CliCommandKind.QueueAddImmich, CliCommandKind.ImmichRun], values => values.First = true),
        Value("--limit", [CliCommandKind.QueueAddImmich, CliCommandKind.ImmichRun], "--limit requires a whole number of 1 or more.", (values, text) =>
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit < 1) return false;
            values.Limit = limit;
            return true;
        }),
        Value("--quality", [CliCommandKind.ConfigInit], "--quality requires archive, standard or compact.", (values, text) =>
        {
            if (!QualityLevels.TryParse(text!, out var level)) return false;
            values.Quality = level;
            return true;
        }),
        Switch("--dry-run", [CliCommandKind.Cleanup], values => values.DryRun = true),
        Switch("--force", [CliCommandKind.QueueAddFile], values => values.Force = true),
        Switch("--ready", [CliCommandKind.Finalize], values => values.Ready = true),
        Switch("--now", [CliCommandKind.StopRun], values => values.StopNow = true),
        Switch("--undo", [CliCommandKind.StopRun], values => values.UndoStop = true),
        Switch("--keep-worker", [CliCommandKind.BackgroundStart], values => values.KeepWorker = true),
        Value("--worker-fate", [CliCommandKind.BackgroundRunner], "--worker-fate requires delete or keep.", (values, text) =>
        {
            if (!TryParseWorkerFate(text!, out var fate)) return false;
            values.WorkerFate = fate;
            return true;
        }),
        Switch(BackgroundRunnerArguments.ExistingWorkerFlag, [CliCommandKind.BackgroundRunner], values => values.ExistingWorker = true),
        Switch("--snapshot", [CliCommandKind.TuiSnapshot], values => values.Snapshot = true),
        Value("--keys", [CliCommandKind.TuiSnapshot], "--keys requires comma-separated characters or up, down, enter, esc, backspace, tab.", (values, text) =>
        {
            if (!SnapshotKeys.TryParse(text!, out var keys)) return false;
            values.SnapshotKeys = keys;
            return true;
        }),
        Value("--size", [CliCommandKind.TuiSnapshot], "--size requires WIDTHxHEIGHT, such as 140x40.", (values, text) =>
        {
            if (!FrameSize.TryParse(text!, out var size)) return false;
            values.Size = size;
            return true;
        }),
        Switch(ProcessingModeOverride.Local.Flag, [CliCommandKind.Run, CliCommandKind.ImmichRun, CliCommandKind.BackgroundStart, CliCommandKind.BackgroundRunner], values => values.Modes.Add(ProcessingModeOverride.Local)),
        Switch(ProcessingModeOverride.Remote.Flag, [CliCommandKind.Run, CliCommandKind.ImmichRun, CliCommandKind.BackgroundStart, CliCommandKind.BackgroundRunner], values => values.Modes.Add(ProcessingModeOverride.Remote)),
    ];

    /// <summary>Options <see cref="CliCommand.Parse"/> answers before any other: valid with every command, and suggested like any other.</summary>
    public static readonly string[] Global = ["--help", "--version"];

    private static readonly Dictionary<string, CliOption> ByName = All.ToDictionary(option => option.Name, StringComparer.Ordinal);

    public static CliOption? Find(string token) => ByName.GetValueOrDefault(token);

    public static bool IsHidden(CliCommandKind kind) => kind is CliCommandKind.BackgroundStart or CliCommandKind.BackgroundRunner or CliCommandKind.TuiSnapshot;

    /// <summary>
    /// Names the public commands only, as the help does, so <c>--local</c> lists <c>run</c> and <c>immich run</c> but not the
    /// background verbs; an option only hidden commands accept names those.
    /// </summary>
    public static string UnsupportedError(CliOption option)
    {
        var kinds = option.Kinds!;
        var named = kinds.Any(kind => !IsHidden(kind)) ? kinds.Where(kind => !IsHidden(kind)).ToArray() : kinds;
        var names = named.Select(CommandName).ToArray();
        var list = names.Length == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";
        return $"{option.Name} is only supported by {list}.";
    }

    /// <summary>
    /// The known option closest to a mistyped one, preferring those this command accepts, or null when none is close. A hidden
    /// option is offered only to the command that accepts it, so a typo on a public command never names one.
    /// </summary>
    /// <remarks>
    /// Close means at most one edit for a name of four letters or fewer after the dashes, two for a longer one: enough for a
    /// slip or a swapped pair (<c>--forse</c>, <c>--froce</c>) without turning a different word into a suggestion.
    /// </remarks>
    public static string? Suggest(string unknown, CliCommandKind kind)
    {
        var candidates = All.Where(option => !option.Hidden || option.AcceptedBy(kind))
            .Select(option => (option.Name, Accepted: option.AcceptedBy(kind)))
            .Concat(Global.Select(name => (Name: name, Accepted: true)))
            .Select(candidate => (candidate.Name, candidate.Accepted, Distance: Distance(unknown.ToLowerInvariant(), candidate.Name)))
            .Where(candidate => candidate.Distance <= (candidate.Name.Length - 2 <= 4 ? 1 : 2))
            .ToList();
        var accepted = candidates.Where(candidate => candidate.Accepted).ToList();
        var pool = accepted.Count > 0 ? accepted : candidates;
        return pool.Count == 0 ? null : pool.MinBy(candidate => candidate.Distance).Name;
    }

    private static CliOption Switch(string name, CliCommandKind[]? kinds, Action<CliOptionValues> set) =>
        new(name, kinds, (values, _) => { set(values); return true; });

    private static CliOption Value(string name, CliCommandKind[]? kinds, string valueError, Func<CliOptionValues, string?, bool> set) =>
        new(name, kinds, set, valueError);

    private static string CommandName(CliCommandKind kind) => kind switch
    {
        CliCommandKind.ConfigInit => "config init",
        CliCommandKind.Doctor => "doctor",
        CliCommandKind.QueueAdd => "queue add",
        CliCommandKind.QueueAddImmich => "queue add immich",
        CliCommandKind.QueueAddFile => "queue add <file>",
        CliCommandKind.Run => "run",
        CliCommandKind.StopRun => "run stop",
        CliCommandKind.QueueList => "queue list",
        CliCommandKind.QueueDrop => "queue drop",
        CliCommandKind.QueueDismiss => "queue dismiss",
        CliCommandKind.Status => "status",
        CliCommandKind.History => "history",
        CliCommandKind.Calibrate => "calibrate",
        CliCommandKind.Cost => "cost",
        CliCommandKind.Finalize => "finalize",
        CliCommandKind.Cleanup => "cleanup",
        CliCommandKind.ImmichRun => "immich run",
        CliCommandKind.BackgroundStart => "background start",
        CliCommandKind.BackgroundRunner => "background runner",
        CliCommandKind.TuiSnapshot => "tui",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No option names this command; give it a name here when one does.")
    };

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

    /// <summary>
    /// Edit distance where swapping two neighbouring letters counts as one edit (optimal string alignment), since a swap
    /// (<c>--hepl</c>) is as common a slip as a wrong letter. Option names are short, so the full table is cheap.
    /// </summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }
}
