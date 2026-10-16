using System.Globalization;
using System.Text.Json;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Status;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli.Features.Status;

/// <summary>
/// <c>status</c>, and the bare <c>video-optimiser</c>: the queue, the run, the worker and what to run next, as lines or
/// <c>--json</c>. What each part holds and which commands are worth running is decided by <see cref="IStatusCard"/>; this holds
/// the wording. The job list is <c>queue list</c>'s. Changed wording goes through the humanizer gate (docs index).
/// </summary>
internal sealed class StatusCommand(IStatusCard card)
{
    public async Task<int> RunAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var report = await card.ReadAsync(configuration.Settings, cancellationToken);
        if (command.Json) Console.WriteLine(JsonSerializer.Serialize(ToJson(report, command), StatusJsonContext.Default.StatusJsonOutput));
        else foreach (var line in Lines(report, command)) Console.WriteLine(line);
        return (int)ExitCode.Success;
    }

    private static IEnumerable<string> Lines(StatusReport report, CliCommand command)
    {
        yield return QueueLine(report.Queue);
        yield return RunLine(report.Run);
        if (report.Worker is { } worker) yield return WorkerLine(worker);
        yield return "Next:";
        var rows = report.Next.Select(step => Words(step, report.Queue)).Select(words => (Command: command.Hint(words.Verb), words.Description)).ToArray();
        var width = rows.Max(row => row.Command.Length) + 3;
        foreach (var row in rows) yield return $"  {row.Command.PadRight(width)}{row.Description}";
    }

    private static string QueueLine(StatusCounts queue)
    {
        var parts = new (int Count, string Label)[]
        {
            (queue.Converting, "converting"), (queue.Waiting, "waiting"), (queue.Stopped, "stopped"), (queue.Held, "held"), (queue.ReadyToFinalise, "ready to finalise")
        }.Where(part => part.Count > 0).Select(part => $"{part.Count.ToString(CultureInfo.InvariantCulture)} {part.Label}").ToArray();
        return parts.Length == 0 ? "Queue: empty." : $"Queue: {string.Join(", ", parts)}.";
    }

    /// <summary>
    /// The command is printed as the lock holder recorded it, which for a run from an older version is not <c>video-optimiser run</c>.
    /// A background run has no command a person typed, so it says what it is.
    /// </summary>
    private static string RunLine(StatusRun? run)
    {
        if (run is null) return "Run: none.";
        var line = $"Run: {(IsBackground(run) ? "background run" : run.Holder.Command)}, started {CliLocks.FormatStarted(run.Holder.StartedUtc)} (process {run.Holder.ProcessId.ToString(CultureInfo.InvariantCulture)})";
        return run.Converting.Count == 0 ? line + "." : $"{line}, converting {string.Join(", ", run.Converting.Select(video => Path.GetFileName(video.SourcePath)))}.";
    }

    private static bool IsBackground(StatusRun run) => run.Holder.Command == LockHolders.BackgroundRun;

    private static string WorkerLine(StatusWorker worker)
    {
        var (info, price, notChecked) = worker.Check;
        if (notChecked is not null)
        {
            // Hetzner's own messages end in a full stop already.
            var why = $"Hetzner wasn't checked: {notChecked.TrimEnd('.')}.";
            return info is null ? $"Worker: none recorded on this PC; {why}" : $"Worker: {info.Name}{Placement(info)}, as recorded on this PC; {why}";
        }
        if (info is null) return "Worker: none.";
        if (info.Status == RemoteWorkerStatuses.PrimaryIpRetained) return $"Worker: {info.Name} is gone from Hetzner, but its IPv4 address still bills.";
        var up = worker.Uptime is { } uptime ? $", up {HumanReadableValues.FormatDuration(uptime)}" : string.Empty;
        var billing = price is { } hourly ? $"billing {HourlyPrice.Format(hourly)} incl. VAT" : "billing hourly";
        return $"Worker: {info.Name}{Placement(info)}{up}, {billing}.";
    }

    /// <summary>What a recovered worker may lack: the type, and so the location it is in.</summary>
    private static string Placement(RemoteWorkerInfo worker) =>
        worker.ServerType.Length == 0 ? string.Empty
        : worker.Location.Length == 0 ? $" ({worker.ServerType})"
        : $" ({worker.ServerType} in {worker.Location})";

    private static (string Verb, string Description) Words(NextStep step, StatusCounts queue) => step switch
    {
        NextStep.FinaliseReady => ("finalize --ready", queue.ReadyToFinalise == 1 ? "finalises the ready video; review it first" : $"finalises the {queue.ReadyToFinalise} ready videos; review them first"),
        NextStep.Run => ("run", queue.Runnable == 1 ? "converts the video in the queue" : $"converts the {queue.Runnable} videos in the queue"),
        NextStep.DeleteWorker => ("worker delete", "stops the worker billing"),
        NextStep.AddFromFolders => ("queue add", "queues new videos from your watch folders"),
        NextStep.AddFromImmich => ("queue add immich", "queues new videos from Immich"),
        NextStep.ListQueue => ("queue list", "lists the videos in the queue"),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null)
    };

    private static StatusJsonOutput ToJson(StatusReport report, CliCommand command)
    {
        var queue = report.Queue;
        return new StatusJsonOutput(
            new StatusJsonQueue(queue.Converting, queue.Waiting, queue.Stopped, queue.Held, queue.ReadyToFinalise),
            report.Run is { } run
                ? new StatusJsonRun(run.Holder.Command, run.Holder.StartedUtc, run.Holder.ProcessId, IsBackground(run), [.. run.Converting.Select(video => new StatusJsonConverting(video.Id.ToString("N"), Path.GetFileName(video.SourcePath), video.Status.ToString()))])
                : null,
            report.Worker is { } worker ? WorkerJson(worker) : null,
            [.. report.Next.Select(step => command.Hint(Words(step, queue).Verb))]);
    }

    private static StatusJsonWorker WorkerJson(StatusWorker worker)
    {
        var (info, price, notChecked) = worker.Check;
        var checkedHetzner = notChecked is null;
        if (info is null) return new StatusJsonWorker(false, checkedHetzner, notChecked, null, null, null, null, null, null, null, null);
        return new StatusJsonWorker(
            true,
            checkedHetzner,
            notChecked,
            info.Name,
            info.ServerId,
            info.Status,
            info.ServerType.Length == 0 ? null : info.ServerType,
            info.Location.Length == 0 ? null : info.Location,
            info.CreatedUtc,
            worker.Uptime is { } uptime ? (long)uptime.TotalSeconds : null,
            price);
    }
}
