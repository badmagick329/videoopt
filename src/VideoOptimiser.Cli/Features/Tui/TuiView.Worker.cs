using Spectre.Console;
using Spectre.Console.Rendering;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli.Features.Tui;

internal static partial class TuiView
{
    private static (string Key, string What)[] WorkerHints((string Key, string What)[] stop)
    {
        var keys = TuiKeys.Worker;
        return [keys.Create.Hint, keys.Delete.Hint, keys.Check.Hint, .. stop, .. Closing()];
    }

    /// <summary>The pane, with what an action on the worker is doing now ("Creating the worker.") on its last row.</summary>
    private static List<IRenderable> WorkerRows(TuiModel m, int width, int capacity)
    {
        var rows = WorkerRowsCore(m, width, capacity);
        if (!m.Busy.TryGetValue(BusyKeys.Worker, out var working)) return rows;
        var busy = new Line().Left(" ", Style.Plain).Flex(working, Theme.Of(Theme.Held)).Build(width);
        if (rows.Count >= capacity) rows[^1] = busy;
        else rows.Add(busy);
        return rows;
    }

    private static List<IRenderable> WorkerRowsCore(TuiModel m, int width, int capacity)
    {
        var rows = new List<IRenderable>();
        IRenderable Pair(string label, string value, Style? valueStyle = null) =>
            new Line().Left($" {label,-13}", Theme.Of(Theme.Dim)).Flex(value, valueStyle ?? Theme.Of(Theme.Text)).Build(width);

        if (m.Worker.Lifecycle.Length == 0)
            return [new Line().Left(" ○ ", Theme.Of(Theme.Dim)).Flex("Reading the worker.", Theme.Of(Theme.Dim)).Build(width)];

        // One line, with the reason: the lifecycle is the only thing that makes a worker managed.
        if (!m.Worker.Managed)
            return [new Line().Left(" ○ ", Theme.Of(Theme.Dim)).Flex($"No managed worker: remoteSsh.lifecycle is \"{m.Worker.Lifecycle}\".", Theme.Of(Theme.Soft)).Build(width)];

        var view = WorkerView.Of(m.Worker);
        if (view.Info is not { } worker)
        {
            rows.Add(new Line().Left(" ○ ", Theme.Of(Theme.Dim)).Flex("No worker", Theme.Of(Theme.Soft, bold: true)).Build(width));
            rows.Add(new Line().Flex(view.NotCheckedReason is { } why
                ? $"   couldn't check: {why}"
                : view.Asking ? "   Asking Hetzner." : "   Nothing is billing.", Theme.Of(view.NotCheckedReason is null ? Theme.Dim : Theme.Held)).Build(width));
            return [.. rows.Take(capacity)];
        }

        var status = worker.Status;
        var settling = status is RemoteWorkerStatuses.Bootstrapping or "initializing" or "starting";
        var statusColor = status == "running" ? Theme.Ready : settling ? Theme.Held : status == RemoteWorkerStatuses.Recorded ? Theme.Soft : Theme.Failed;
        rows.Add(new Line()
            .Left(settling ? $" {SpinnerFrame(m)} " : " ● ", Theme.Of(statusColor))
            .Flex(worker.Name, Theme.Of(Theme.Text, bold: true))
            .Right(RemoteWorkerStatuses.Describe(status) + " ", Theme.Of(statusColor))
            .Build(width));
        var place = string.Join(" · ", new[] { worker.ServerType, worker.Location }.Where(part => part.Length > 0));
        rows.Add(new Line().Flex(place.Length > 0 ? "   " + place : string.Empty, Theme.Of(Theme.Dim)).Build(width));
        rows.Add(new Line().Build(width));
        if (view.Uptime(m.Now) is { } uptime) rows.Add(Pair("Uptime", HumanReadableValues.FormatDuration(uptime)));
        rows.Add(view.Price is { } price
            ? Pair("Price", $"{HourlyPrice.Format(price)} incl. VAT")
            : Pair("Price", view.Asking ? "asking Hetzner" : "unknown", Theme.Of(Theme.Dim)));
        if (view.Cost(m.Now) is { } cost) rows.Add(Pair("Cost so far", TuiText.Eur(cost), Theme.Of(Theme.Text, bold: true)));
        if (view.NotCheckedReason is { } reason) rows.Add(Pair("Hetzner", $"couldn't check: {reason}", Theme.Of(Theme.Held)));
        return [.. rows.Take(capacity)];
    }
}
