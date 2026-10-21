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
        if (TuiKeys.Anywhere.Start.Is(c)) return StartKey(m);
        if (TuiKeys.Anywhere.StopAfter.Is(c) || TuiKeys.Anywhere.StopNow.Is(c)) return StopKey(m, c);
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
        var keys = TuiKeys.Queue;
        if (keys.Add.Is(c)) return AddKey(m);
        if (!(keys.Hold.Is(c) || keys.Release.Is(c) || keys.Drop.Is(c) || keys.RunNext.Is(c) || keys.Retry.Is(c))) return TuiStep.Of(m);
        if (m.Ordered().FirstOrDefault(video => video.Id == m.QueueSelection) is not { } v) return Told(m, "Nothing is selected.");
        var q = v.Name;
        if (keys.Hold.Is(c))
        {
            if (v.Group == VideoGroup.Queue) return Emit(m, new QueueActionEffect(QueueAction.Hold, v.Id, q));
            return Told(m, v.Group switch
            {
                VideoGroup.Held => $"{q} is already held.",
                VideoGroup.ConvertingNow => ConvertingNow(m, q, stopAfterVideoWorks: false),
                _ => "Only a waiting or stopped video can be held."
            });
        }
        if (keys.Release.Is(c))
            return v.Group == VideoGroup.Held ? Emit(m, new QueueActionEffect(QueueAction.Release, v.Id, q)) : Told(m, $"{q} isn't held.");
        if (keys.RunNext.Is(c))
        {
            // Already converting, so it is ahead of everything else; as queue next says, there is nothing to stop.
            if (v.Group == VideoGroup.ConvertingNow) return Told(m, $"{q} is converting now.");
            return v.Group is VideoGroup.Queue or VideoGroup.Held
                ? Emit(m, new QueueActionEffect(QueueAction.Next, v.Id, q))
                : Told(m, "Only a waiting, stopped or held video can run next.");
        }
        if (keys.Drop.Is(c))
        {
            return v.Group switch
            {
                VideoGroup.ConvertingNow => Told(m, ConvertingNow(m, q, stopAfterVideoWorks: true)),
                VideoGroup.Failed => DismissKey(m, v),
                VideoGroup.Ready => Confirming(m, $"Drop {q}?", "The original is untouched. Its AV1 stays until cleanup deletes it.", new QueueActionEffect(QueueAction.Drop, v.Id, q)),
                _ => Confirming(m, $"Drop {q}?", "It leaves the queue. The original is untouched.", new QueueActionEffect(QueueAction.Drop, v.Id, q))
            };
        }
        return v.Group == VideoGroup.Failed
            ? Confirming(m, $"Retry {q}?", "A fresh job starts from scratch. The failed one stays in history.", new QueueActionEffect(QueueAction.Retry, v.Id, q))
            : Told(m, "Only a failed video can be retried.");
    }

    private const string DismissDetail = "Dismissed failures leave this list and stay in history. Their originals stay skipped; queue retry <job-id> still queues one again.";

    /// <summary>
    /// A failed video has nothing to drop, so d dismisses it (VOPT-115). With more than one failure the choice also offers all
    /// of them, which is how a backlog of old failures goes in one press.
    /// </summary>
    private static TuiStep DismissKey(TuiModel m, Video video)
    {
        var one = new QueueActionEffect(QueueAction.Dismiss, video.Id, video.Name);
        if (m.Failed.Count == 1) return Confirming(m, $"Dismiss {video.Name}?", DismissDetail, one);
        // The group shows at most MostFailed; at the cap there may be more, and all of them go.
        var all = m.Failed.Count < TuiSnapshotReader.MostFailed ? $"Dismiss all {m.Failed.Count} failures" : "Dismiss every failure";
        return Asking(m, new TuiChoice("Dismiss failures", DismissDetail, [new ChoiceOption($"Dismiss {video.Name}", one), new ChoiceOption(all, new DismissAllFailures())], 0));
    }

    private static string ConvertingNow(TuiModel m, string name, bool stopAfterVideoWorks) => $"{name} is converting now. {StopWords.HowToStop(m.Run, stopAfterVideoWorks)}";

    // ---- worker -----------------------------------------------------------------------------

    private const string NoManagedWorker = "There is no managed worker: remoteSsh.lifecycle isn't hetzner.";

    private static TuiStep WorkerKey(TuiModel m, char c)
    {
        var keys = TuiKeys.Worker;
        if (keys.Create.Is(c))
        {
            if (!m.Worker.Managed) return Told(m, NoManagedWorker);
            if (WorkerView.Of(m.Worker).Info is { } up) return Told(m, $"The worker {up.Name} is already up.");
            return Emit(m, new LoadWorkerOptions(WorkerOptionsPurpose.Create));
        }
        if (keys.Delete.Is(c))
        {
            if (!m.Worker.Managed) return Told(m, NoManagedWorker);
            if (WorkerView.Of(m.Worker).Info is not { } worker) return Told(m, "There is no worker to delete.");
            return Confirming(m, $"Delete the worker {worker.Name}?", m.Run is null
                ? "It stops billing at once."
                : "It stops billing at once. The video converting now stops and starts again from its stage on the next worker.", new DeleteWorker());
        }
        return TuiStep.Of(m);
    }

    private static string PriceOf(RemoteWorkerCreationOption option) => option.HourlyGross is { } price ? HourlyPrice.Format(price) : "price not listed";

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static TuiStep OnWorkerOptions(TuiModel m, WorkerOptionsMsg msg)
    {
        m = m with { Busy = m.Busy.Remove(BusyKeys.Worker) };
        var found = msg.Options is { Count: > 0 } options ? options : null;
        var why = found is not null ? null : msg.Error ?? "No server qualifies.";

        if (msg.Purpose == WorkerOptionsPurpose.Create)
        {
            if (found is null)
            {
                var lines = why!.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).Select(line => new LogLine(LogKind.Error, line));
                return TuiStep.Of(Tell(AppendLines(m, lines), FirstLine(why)));
            }
            var create = new TuiChoice("Create a worker", "Pick the server. Prices include VAT, and it bills hourly until it is deleted.",
                [.. found.Select(option => new ChoiceOption($"{option.ServerType}  {option.Location}", new CreateWorker(option), Hint: PriceOf(option)))], 0);
            return TuiStep.Of(OpenAnswer(m, create, ServerListDropped(TuiKeys.Worker.Create)));
        }

        // Starting a run: every server, the first selected (the one `worker create` would take), and always the way out.
        ImmutableList<ChoiceOption> choices = [.. (found ?? []).Select(option => new ChoiceOption($"{option.ServerType}  {option.Location}", null, FateChoice(option), PriceOf(option)))];
        choices = choices.Add(NoWorkerOption);
        var detail = why is null ? "Pick the server the run uses. Prices include VAT." : $"Couldn't list the servers: {FirstLine(why)}";
        if (why is not null) m = AppendLines(m, [new LogLine(LogKind.Warning, $"Couldn't list the servers: {FirstLine(why)}")]);
        return TuiStep.Of(OpenAnswer(m, new TuiChoice("Which worker converts?", detail, choices, 0), ServerListDropped(TuiKeys.Anywhere.Start)));
    }

    private static string ServerListDropped(TuiKey again) => $"The server list came in while a question was open. Press {again.Name} again.";

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
        // VOPT-154: a delete or create from the worker pane would change the worker under the choice, so "Use X" could name a
        // server that is about to be gone. The runner refuses to replace a missing chosen worker too; this saves the wasted launch.
        if (m.Busy.TryGetValue(BusyKeys.Worker, out var working)) return Told(m, WorkerBusyAtStart(working));

        if (WorkerView.Of(m.Worker).Info is { } worker)
        {
            return Asking(m, new TuiChoice("Which worker converts?", "A worker is up already.", [new ChoiceOption($"Use {worker.Name}", null, FateChoice(null)), NoWorkerOption], 0));
        }
        return Emit(m, new LoadWorkerOptions(WorkerOptionsPurpose.Start));
    }

    /// <summary>What <c>s</c> says while the worker pane's action goes; the server list keeps its own busy text.</summary>
    private static string WorkerBusyAtStart(string working) => working switch
    {
        DeleteWorker.Text => "The worker is being deleted. Start the run once it's gone.",
        CreateWorker.Text => "The worker is being created. Start the run once it's up.",
        _ => working
    };

    // ---- activity: stopping and the worker's fate -------------------------------------------

    private static TuiStep ActivityKey(TuiModel m, char c)
    {
        if (!TuiKeys.Activity.Fate.Is(c)) return TuiStep.Of(m);
        if (m.Run is not { } run) return Told(m, "No run is going.");
        if (!run.IsBackground) return Told(m, "That run asks about its worker in its own window.");
        // With no managed worker the fate means nothing: the run has no worker to delete or keep.
        if (!m.Worker.Managed) return Told(m, "There is no worker whose fate to set.");
        var current = FateOf(m) ?? WorkerFateChoice.Delete;
        return Asking(m, new TuiChoice("When the run ends", "What happens to the worker. The run reads this when it ends, so a change counts.",
            [new ChoiceOption("Delete the worker", new SetWorkerFate(WorkerFateChoice.Delete)), new ChoiceOption("Keep the worker", new SetWorkerFate(WorkerFateChoice.Keep))],
            current == WorkerFateChoice.Keep ? 1 : 0));
    }

    /// <summary>
    /// Stopping is the run's, not a pane's: x and X work wherever the cursor is, like s. x toggles a stop after this video
    /// (VOPT-189): asked, it withdraws without a dialog, since carrying on loses nothing; a stop now is final.
    /// </summary>
    private static TuiStep StopKey(TuiModel m, char c)
    {
        if (m.Run is not { } run) return Told(m, "No run is going.");
        if (!run.IsBackground) return Told(m, "That run is in another window; only Ctrl+C there stops it.");
        if (TuiKeys.Anywhere.StopAfter.Is(c))
        {
            if (m.Requests is { StopNow: true }) return Told(m, TuiActions.StoppingNowFinal);
            if (m.Requests is { StopAfterThisVideo: true }) return Emit(m, new WithdrawStop());
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
        var keys = TuiKeys.Review;
        var opens = keys.OpenOriginal.Is(c) || keys.OpenAv1.Is(c);
        var changes = keys.Finalise.Is(c) || keys.FinaliseAll.Is(c) || keys.Reject.Is(c);
        if (!(opens || changes)) return TuiStep.Of(m);
        if (changes && m.Finalising is { } batch) return WhileFinalising(m, c, batch);
        var ready = m.Ready();
        if (ready.Count == 0) return Told(m, "Nothing is ready to finalise.");
        if (keys.FinaliseAll.Is(c)) return Emit(m, new PrepareFinalise(null));
        var video = ready.FirstOrDefault(candidate => candidate.Id == m.ReviewSelection) ?? ready[0];
        if (keys.Reject.Is(c)) return RejectKey(m, video);
        return keys.Finalise.Is(c) ? Emit(m, new PrepareFinalise(video.Id)) : Emit(m, new OpenFile(video.Id, Original: keys.OpenOriginal.Is(c), video.Name));
    }

    // ---- stopping a finalise ----------------------------------------------------------------

    /// <summary>What the quit dialog adds while Esc would stop a finalise batch, the clean way out of a long one.</summary>
    internal const string EscStopsTheFinalise = "To stop the finalise cleanly, cancel this and press Esc. It stops after the video it's on.";

    /// <summary>
    /// Esc stops a finalise batch after the video it is on, without asking: nothing is lost, since that video always finishes
    /// and the rest stay ready. Like <c>x</c> on a run, a second press says the stop is asked already.
    /// </summary>
    private static TuiStep StopFinaliseKey(TuiModel m)
    {
        if (m.Finalising is not { } batch) return TuiStep.Of(m);
        if (batch.Left <= 1) return Told(m, "Only the video being finalised is left, and it always finishes.");
        if (batch.StopAsked) return Told(m, "A stop is already asked of the finalise.");
        m = m with
        {
            Finalising = batch with { StopAsked = true },
            Busy = m.Busy.SetItem(BusyKeys.Finalise, "Stopping the finalise after this video.")
        };
        return new TuiStep(Tell(m, "The finalise stops after this video."), [new StopFinalise()]);
    }

    /// <summary>
    /// <c>f</c>, <c>F</c> and <c>r</c> while a finalise batch is out answer at once, before any question: a second finalise
    /// could only be refused once its confirmation was answered, and <c>queue reject</c> needs the database-wide finalise lock
    /// the batch holds, so rejecting waits on every row. A row of the batch says where it is in it.
    /// </summary>
    private static TuiStep WhileFinalising(TuiModel m, char c, FinaliseBatch batch)
    {
        var ready = m.Ready();
        var video = ready.FirstOrDefault(candidate => candidate.Id == m.ReviewSelection) ?? ready.FirstOrDefault();
        if (video is not null && video.Id == batch.Current) return Told(m, $"{video.Name} is being finalised now.");
        if (video is not null && batch.IsWaiting(video.Id)) return Told(m, $"{video.Name} is queued to be finalised.");
        return Told(m, TuiKeys.Review.Reject.Is(c) ? "A finalise is going. You can reject once it ends." : "A finalise is going. You can start another once it ends.");
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
                return TuiStep.Of(m with { Dialog = choice with { Selected = at } });
            case ConsoleKey.Escape:
                return TuiStep.Of(m with { Dialog = null });
            case ConsoleKey.Enter:
                var option = choice.Options[choice.Selected];
                m = m with { Dialog = option.Next };
                return option.Effect is { } effect ? Emit(m, effect) : TuiStep.Of(m);
            default:
                return TuiStep.Of(m);
        }
    }

    // ---- what the effects report ------------------------------------------------------------

    private static TuiStep OnActionDone(TuiModel m, ActionDoneMsg done)
    {
        m = AppendLines(m with { Busy = m.Busy.Remove(done.Key) }, done.Result.Lines);
        if (done.Key == BusyKeys.Finalise) m = m with { Finalising = null };
        if (done.Result.Notice is { } notice) m = Tell(m, notice);
        // What Hetzner said about the worker is stale once an action touched it: forget it, drop an answer still on its way,
        // and ask again at the next tick.
        if (done.Result.RefreshWorker) m = m with { Worker = m.Worker with { Generation = m.Worker.Generation + 1, Check = null, CheckInFlight = false, CheckAskedAt = null } };
        return new TuiStep(m, [new RefreshSnapshot()]);
    }

    // A finalise's progress. Both are dropped with no batch out: one that comes after the batch's ActionDoneMsg has nothing to move.

    private static TuiModel OnFinaliseStarted(TuiModel m, FinaliseStartedMsg started) =>
        m.Finalising is { } batch ? m with { Finalising = batch with { Current = started.Id } } : m;

    private static TuiModel OnFinaliseEnded(TuiModel m, FinaliseEndedMsg ended)
    {
        if (m.Finalising is not { } batch) return m;
        m = m with
        {
            Finalising = batch with
            {
                Current = null,
                Ended = batch.Ended + 1,
                Failed = batch.Failed + (ended.Succeeded ? 0 : 1),
                SavedBytes = batch.SavedBytes + ended.SavedBytes
            }
        };
        return AppendLines(m, [ended.Line]);
    }

    private static TuiModel OnPrepared(TuiModel m, PreparedMsg prepared)
    {
        m = m with { Busy = m.Busy.Remove(prepared.Key) };
        if (prepared.Lines is { } lines) m = AppendLines(m, lines);
        if (prepared.Confirm is { } confirm)
        {
            var again = prepared.Asked.Id is null ? TuiKeys.Review.FinaliseAll : TuiKeys.Review.Finalise;
            m = OpenAnswer(m, confirm, $"The finalise question came in while another was open. Press {again.Name} again.");
        }
        return prepared.Notice is { } notice ? Tell(m, notice) : m;
    }
}
