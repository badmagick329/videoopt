using System.Globalization;
using System.Text.Json;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Cost;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli.Features.Cost;

/// <summary>
/// <c>cost [--json]</c>: what the space freed on Hetzner workers cost, lifetime first, then one line per run. Per-video
/// figures are in <c>--json</c> only, so a long history stays readable. It writes nothing and always exits 0. Changed wording
/// goes through the humanizer gate (docs index).
/// </summary>
internal sealed class CostCommand(ICostReport report)
{
    private const string Gap = "  ";

    public async Task<int> RunAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var cost = await report.ReadAsync(configuration.Settings.Database.Path, cancellationToken);
        if (command.Json) CliConsole.WriteLine(JsonSerializer.Serialize(ToJson(cost), CostJsonContext.Default.CostJsonOutput));
        else foreach (var line in Lines(cost, command)) CliConsole.WriteLine(line);
        return (int)ExitCode.Success;
    }

    internal static IEnumerable<string> Lines(CostReport cost, CliCommand command)
    {
        if (cost.NothingRecorded)
        {
            yield return "Nothing to show yet: the cost appears after a run on a Hetzner worker.";
            yield break;
        }

        var lifetime = cost.Lifetime;
        yield return $"All time: {LifetimeFigure(lifetime)} {Bill(lifetime)}";
        foreach (var note in Notes(lifetime)) yield return note;
        if (cost.Runs.Count == 0) yield break;

        yield return "Runs, newest first:";
        var rows = cost.Runs.Select(RunColumns).ToArray();
        var widths = Enumerable.Range(0, rows[0].Length - 1).Select(column => rows.Max(row => row[column].Length)).ToArray();
        foreach (var row in rows) yield return Gap + string.Join(Gap, row.Select((cell, column) => column < widths.Length ? cell.PadRight(widths[column]) : cell));
        yield return $"For each video's figures, run '{command.Hint("cost --json")}'.";
    }

    // The lifetime's sentences are shared with the TUI's cost page, so the two say the same thing about the same figures.

    internal static string LifetimeFigure(CostLifetime lifetime) =>
        lifetime.EurPerTerabyte is { } perTerabyte ? $"{CostPrice.PerTerabyte(perTerabyte)} freed." : "nothing freed yet.";

    /// <summary>Why the lifetime figure is not final or not whole, one sentence each, in the order the flags are listed.</summary>
    internal static IEnumerable<string> Notes(CostLifetime lifetime)
    {
        if (lifetime.StillBilling) yield return "A worker is still up: its bill counts to now and grows until the worker is deleted.";
        if (lifetime.EndEstimated) yield return "A worker was deleted outside video-optimiser, so its bill ends when it was last seen up.";
        if (lifetime.PriceMissing) yield return "Hetzner listed no price for a worker, so its bill and its videos are left out.";
    }

    internal static string Bill(CostLifetime lifetime)
    {
        var workers = lifetime.Workers == 1 ? "1 worker" : $"{lifetime.Workers.ToString(CultureInfo.InvariantCulture)} workers";
        var bill = $"Hetzner billed {CostPrice.Amount(lifetime.BilledEur)} for {workers}";
        if (lifetime.FreedBytes > 0) bill += $", which freed {HumanReadableValues.FormatSize(lifetime.FreedBytes)}";
        if (lifetime.WaitingBytes > 0) bill += $"; {HumanReadableValues.FormatSize(lifetime.WaitingBytes)} {(lifetime.FreedBytes > 0 ? "more " : "")}is waiting for review";
        return bill + ".";
    }

    private static string[] RunColumns(CostRun run) =>
    [
        When(run.StartedUtc, run.EndUtc),
        run.WorkerName,
        HumanReadableValues.FormatDuration(run.WorkerTime),
        run.Videos.Count == 1 ? "1 video" : $"{run.Videos.Count.ToString(CultureInfo.InvariantCulture)} videos",
        run.Eur is { } eur ? CostPrice.Amount(eur) : "no price",
        Saved(run),
        CostPrice.PerTerabyte(run.EurPerTerabyte)
    ];

    private static string Saved(CostRun run)
    {
        var freed = run.FreedBytes > 0 ? $"{HumanReadableValues.FormatSize(run.FreedBytes)} freed" : "nothing freed";
        return run.WaitingBytes > 0 ? $"{freed}, {HumanReadableValues.FormatSize(run.WaitingBytes)} waiting for review" : freed;
    }

    /// <summary>Local time, with the end's date only when the run crossed midnight.</summary>
    internal static string When(DateTimeOffset startedUtc, DateTimeOffset endUtc)
    {
        var start = startedUtc.ToLocalTime();
        var end = endUtc.ToLocalTime();
        var endText = end.Date == start.Date ? end.ToString("HH:mm", CultureInfo.InvariantCulture) : end.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return $"{start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} to {endText}";
    }

    private static CostJsonOutput ToJson(CostReport cost)
    {
        var lifetime = cost.Lifetime;
        return new CostJsonOutput(
            new CostJsonLifetime(Round(lifetime.BilledEur), lifetime.FreedBytes, lifetime.WaitingBytes, Round(lifetime.EurPerTerabyte), lifetime.Workers, lifetime.StillBilling, lifetime.EndEstimated, lifetime.PriceMissing),
            [.. cost.Runs.Select(run => new CostJsonRun(
                run.Id.ToString("N"), run.StartedUtc.ToUniversalTime(), run.EndUtc.ToUniversalTime(), run.WorkerServerId, run.WorkerName, run.HourlyGross,
                Seconds(run.WorkerTime), Round(run.Eur), run.FreedBytes, run.WaitingBytes, Round(run.EurPerTerabyte), run.Videos.Count,
                [.. run.Videos.Select(video => new CostJsonVideo(
                    video.JobId.ToString("N"), video.FileName, Seconds(video.WorkerTime), Round(video.Eur), video.SavedBytes, State(video.State), video.Status.ToString(),
                    Round(video.EurPerTerabyte), video.PriceMissing))]))]);
    }

    private static string State(CostVideoState state) => state switch
    {
        CostVideoState.Freed => "freed",
        CostVideoState.WaitingForReview => "waitingForReview",
        _ => "nothingSaved"
    };

    private static long Seconds(TimeSpan duration) => (long)duration.TotalSeconds;

    // The figures are not rounded to the hour or the cent; 4 places keep a fraction of a cent without 28 digits of division.
    private static decimal Round(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static decimal? Round(decimal? value) => value is { } amount ? Round(amount) : null;
}
