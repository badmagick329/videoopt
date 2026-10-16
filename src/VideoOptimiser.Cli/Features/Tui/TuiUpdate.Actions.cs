using System.Collections.Immutable;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Tui;

// The action keys, the dialogs they open and the messages their effects come back as. A key that cannot apply says why in the
// footer; one that can either acts at once (hold, release and next are undoable) or asks first. Nothing here does anything but
// build the next model and the effects to run.

internal static partial class TuiUpdate
{
    private static TuiStep ActionKey(TuiModel m, char c)
    {
        if (c == 's') return StartKey(m);
        if (c is 'x' or 'X') return StopKey(m, c);
        return m.Focus switch
        {
            Pane.Queue => QueueKey(m, c),
            Pane.Worker => WorkerKey(m, c),
            Pane.Activity => ActivityKey(m, c),
            _ => ReviewKey(m, c)
        };
    }

    // ---- queue ------------------------------------------------------------------------------

    private static TuiStep QueueKey(TuiModel m, char c)
    {
        if (c is not ('h' or 'r' or 'd' or 'n' or 't')) return TuiStep.Of(m);
        if (m.Ordered().FirstOrDefault(video => video.Id == m.QueueSelection) is not { } v) return Told(m, "Nothing is selected.");
        var q = v.Name;
        switch (c)
        {
            case 'h':
                if (v.Group == VideoGroup.Queue) return Emit(m, new QueueActionEffect(QueueAction.Hold, v.Id, q));
                return Told(m, v.Group switch
                {
                    VideoGroup.Held => $"{q} is already held.",
                    VideoGroup.ConvertingNow => ConvertingNow(m, q),
                    _ => "Only a waiting or stopped video can be held."
                });
            case 'r':
                return v.Group == VideoGroup.Held ? Emit(m, new QueueActionEffect(QueueAction.Release, v.Id, q)) : Told(m, $"{q} isn't held.");
            case 'n':
                if (v.Group == VideoGroup.ConvertingNow) return Told(m, ConvertingNow(m, q));
                return v.Group is VideoGroup.Queue or VideoGroup.Held
                    ? Emit(m, new QueueActionEffect(QueueAction.Next, v.Id, q))
                    : Told(m, "Only a waiting, stopped or held video can run next.");
            case 'd':
                return v.Group switch
                {
                    VideoGroup.ConvertingNow => Told(m, ConvertingNow(m, q)),
                    VideoGroup.Failed => Told(m, $"{q} has failed, so there is nothing to drop."),
                    VideoGroup.Ready => Confirming(m, $"Drop {q}?", "The original is untouched. Its AV1 stays until cleanup deletes it.", new QueueActionEffect(QueueAction.Drop, v.Id, q)),
                    _ => Confirming(m, $"Drop {q}?", "It leaves the queue. The original is untouched.", new QueueActionEffect(QueueAction.Drop, v.Id, q))
                };
            default:
                return v.Group == VideoGroup.Failed
                    ? Confirming(m, $"Retry {q}?", "A fresh job starts from scratch. The failed one stays in history.", new QueueActionEffect(QueueAction.Retry, v.Id, q))
                    : Told(m, "Only a failed video can be retried.");
        }
    }

    private static string ConvertingNow(TuiModel m, string name) => $"{name} is converting now. {StopWords.HowToStop(m.Run)}";

    // ---- worker -----------------------------------------------------------------------------

    private const string NoManagedWorker = "There is no managed worker: remoteSsh.lifecycle isn't hetzner.";

    private static TuiStep WorkerKey(TuiModel m, char c)
    {
        switch (c)
        {
            case 'r':
                if (!m.Worker.Managed) return Told(m, "There is no managed worker to check.");
                if (m.Worker.CheckInFlight) return Told(m, "Already asking Hetzner.");
                return AskHetzner(Tell(m, AskingHetzner));
            case 'c':
                if (!m.Worker.Managed) return Told(m, NoManagedWorker);
                if (WorkerView.Of(m.Worker).Info is { } up) return Told(m, $"The worker {up.Name} is already up.");
                return Emit(m, new LoadWorkerOptions(WorkerOptionsPurpose.Create));
            case 'd':
                if (!m.Worker.Managed) return Told(m, NoManagedWorker);
                if (WorkerView.Of(m.Worker).Info is not { } worker) return Told(m, "There is no worker to delete.");
                return Confirming(m, $"Delete the worker {worker.Name}?", m.Run is null
                    ? "It stops billing at once."
                    : "It stops billing at once. The video converting now stops and starts again from its stage on the next worker.", new DeleteWorker());
            default:
                return TuiStep.Of(m);
        }
    }

    private static string PriceOf(RemoteWorkerCreationOption option) => option.HourlyGross is { } price ? HourlyPrice.Format(price) : "price not listed";

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static TuiStep OnWorkerOptions(TuiModel m, WorkerOptionsMsg msg)
    {
        m = m with { Busy = m.Busy.Remove("worker") };
        var found = msg.Options is { Count: > 0 } options ? options : null;
        var why = found is not null ? null : msg.Error ?? "No server qualifies.";

        if (msg.Purpose == WorkerOptionsPurpose.Create)
        {
            if (found is null)
            {
                var lines = why!.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).Select(line => new LogLine(LogKind.Error, line));
                return TuiStep.Of(Tell(AppendLines(m, lines), FirstLine(why)));
            }
            return TuiStep.Of(m with
            {
                Choice = new TuiChoice("Create a worker", "Pick the server. Prices include VAT, and it bills hourly until it is deleted.",
                    [.. found.Select(option => new ChoiceOption($"{option.ServerType}  {option.Location}", new CreateWorker(option), Hint: PriceOf(option)))], 0)
            });
        }

        // Starting a run: every server, the first selected (the one `worker create` would take), and always the way out.
        ImmutableList<ChoiceOption> choices = [.. (found ?? []).Select(option => new ChoiceOption($"{option.ServerType}  {option.Location}", null, FateChoice(option), PriceOf(option)))];
        choices = choices.Add(NoWorkerOption);
        var detail = why is null ? "Pick the server the run uses. Prices include VAT." : $"Couldn't list the servers: {FirstLine(why)}";
        if (why is not null) m = AppendLines(m, [new LogLine(LogKind.Warning, $"Couldn't list the servers: {FirstLine(why)}")]);
        return TuiStep.Of(m with { Choice = new TuiChoice("Which worker converts?", detail, choices, 0) });
    }

    private static ChoiceOption NoWorkerOption { get; } =
        new("No worker: convert on this PC", new StartRun(new StartPlan(null, WorkerFateChoice.Delete, ProcessingModeOverride.Local)));

    private static TuiChoice FateChoice(RemoteWorkerCreationOption? create) => new(
        "When the run ends",
        "A kept worker bills hourly until you delete it.",
        [
            new ChoiceOption("Delete the worker", new StartRun(new StartPlan(create, WorkerFateChoice.Delete, ProcessingModeOverride.Remote))),
            new ChoiceOption("Keep the worker", new StartRun(new StartPlan(create, WorkerFateChoice.Keep, ProcessingModeOverride.Remote)))
        ],
        0);

    // ---- starting a run ---------------------------------------------------------------------

    private static TuiStep StartKey(TuiModel m)
    {
        if (m.Run is not null) return Told(m, "A run is going already.");
        if (m.Count(VideoGroup.Queue) == 0) return Told(m, "No video is waiting to convert.");
        if (m.Worker.Lifecycle.Length == 0) return Told(m, "Still reading the worker.");
        // Not hetzner: nothing to ask, and the config's mode stands.
        if (!m.Worker.Managed) return Emit(m, new StartRun(new StartPlan(null, WorkerFateChoice.Delete, null)));

        if (WorkerView.Of(m.Worker).Info is { } worker)
        {
            return TuiStep.Of(m with
            {
                Choice = new TuiChoice("Which worker converts?", "A worker is up already.", [new ChoiceOption($"Use {worker.Name}", null, FateChoice(null)), NoWorkerOption], 0)
            });
        }
        return Emit(m, new LoadWorkerOptions(WorkerOptionsPurpose.Start));
    }

    // ---- activity: stopping and the worker's fate -------------------------------------------

    private static TuiStep ActivityKey(TuiModel m, char c)
    {
        if (c != 'w') return TuiStep.Of(m);
        if (m.Run is not { } run) return Told(m, "No run is going.");
        if (run.Command != LockHolders.BackgroundRun) return Told(m, "That run asks about its worker in its own window.");
        // With no managed worker the fate means nothing: the run has no worker to delete or keep.
        if (!m.Worker.Managed) return Told(m, "There is no worker whose fate to set.");
        var current = FateOf(m) ?? WorkerFateChoice.Delete;
        return TuiStep.Of(m with
        {
            Choice = new TuiChoice("When the run ends", "What happens to the worker. The run reads this when it ends, so a change counts.",
                [new ChoiceOption("Delete the worker", new SetWorkerFate(WorkerFateChoice.Delete)), new ChoiceOption("Keep the worker", new SetWorkerFate(WorkerFateChoice.Keep))],
                current == WorkerFateChoice.Keep ? 1 : 0)
        });
    }

    /// <summary>Stopping is the run's, not a pane's: x and X work wherever the cursor is, like s.</summary>
    private static TuiStep StopKey(TuiModel m, char c)
    {
        if (m.Run is not { } run) return Told(m, "No run is going.");
        if (run.Command != LockHolders.BackgroundRun) return Told(m, "That run is in another window; only Ctrl+C there stops it.");
        if (c == 'x')
        {
            if (m.Requests is { StopNow: true } or { StopAfterThisVideo: true }) return Told(m, "A stop is already asked of the run.");
            return Emit(m, new StopRun(Now: false));
        }
        if (m.Requests is { StopNow: true }) return Told(m, "The run is already stopping now.");
        return Confirming(m, "Stop the run now?", "The video converting now stops and starts again from its stage next time.", new StopRun(Now: true));
    }

    /// <summary>The fate a background run was started with, as the event file says, until the requests row (which a later change lands in) answers.</summary>
    internal static WorkerFateChoice? FateOf(TuiModel m)
    {
        if (m.Requests?.WorkerFate is { } fate) return fate;
        return Enum.TryParse<WorkerFateChoice>(m.Activity.Started?.Fate, out var started) ? started : null;
    }

    // ---- review -----------------------------------------------------------------------------

    private static TuiStep ReviewKey(TuiModel m, char c)
    {
        if (c is not ('o' or 'a' or 'f' or 'F' or 'r')) return TuiStep.Of(m);
        var ready = m.Ready();
        if (ready.Count == 0) return Told(m, "Nothing is ready to finalise.");
        if (c == 'F') return Emit(m, new PrepareFinalise(null));
        var video = ready.FirstOrDefault(candidate => candidate.Id == m.ReviewSelection) ?? ready[0];
        if (c == 'r') return RejectKey(m, video);
        return c == 'f' ? Emit(m, new PrepareFinalise(video.Id)) : Emit(m, new OpenFile(video.Id, Original: c == 'o', video.Name));
    }

    /// <summary>
    /// Rejecting deletes the AV1 for good (the original stays), so it asks first. A video partway through finalising is
    /// finalize's, as for <c>queue reject</c>; the verb refuses the other states with its own words.
    /// </summary>
    private static TuiStep RejectKey(TuiModel m, Video video)
    {
        if (video.Status != JobStatus.ReadyToFinalize) return Told(m, $"{video.Name} is part way through finalising, so it can't be rejected.");
        var detail = video.Immich
            ? "Its AV1 and downloaded copy are deleted at once. The original stays in Immich."
            : "Its AV1 is deleted at once. The original is untouched.";
        return Confirming(m, $"Reject {video.Name}?", detail, new QueueActionEffect(QueueAction.Reject, video.Id, video.Name));
    }

    // ---- lists ------------------------------------------------------------------------------

    private static TuiStep OnChoiceKey(TuiModel m, TuiChoice choice, ConsoleKey key)
    {
        switch (key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.DownArrow:
                var at = Math.Clamp(choice.Selected + (key == ConsoleKey.UpArrow ? -1 : 1), 0, choice.Options.Count - 1);
                return TuiStep.Of(m with { Choice = choice with { Selected = at } });
            case ConsoleKey.Escape:
                return TuiStep.Of(m with { Choice = null });
            case ConsoleKey.Enter:
                var option = choice.Options[choice.Selected];
                m = m with { Choice = option.Next };
                return option.Effect is { } effect ? Emit(m, effect) : TuiStep.Of(m);
            default:
                return TuiStep.Of(m);
        }
    }

    // ---- what the effects report ------------------------------------------------------------

    private static TuiStep OnActionDone(TuiModel m, ActionDoneMsg done)
    {
        m = AppendLines(m with { Busy = m.Busy.Remove(done.Key) }, done.Result.Lines);
        if (done.Result.Notice is { } notice) m = Tell(m, notice);
        // What Hetzner said about the worker is stale once an action touched it: forget it, drop an answer still on its way,
        // and ask again at the next tick.
        if (done.Result.RefreshWorker) m = m with { Worker = m.Worker with { Generation = m.Worker.Generation + 1, Check = null, CheckInFlight = false, CheckAskedAt = null } };
        return new TuiStep(m, [new RefreshSnapshot()]);
    }

    private static TuiModel OnPrepared(TuiModel m, PreparedMsg prepared)
    {
        m = m with { Busy = m.Busy.Remove(prepared.Key) };
        if (prepared.Confirm is { } confirm) m = m with { Confirm = confirm };
        return prepared.Notice is { } notice ? Tell(m, notice) : m;
    }
}
