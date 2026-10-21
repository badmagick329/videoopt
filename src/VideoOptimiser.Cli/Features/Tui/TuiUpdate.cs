using System.Collections.Immutable;
using VideoOptimiser.Application.Features.Run;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// <c>Update(model, msg)</c>: the one place state changes. It never reads the clock, the console or a file: the clock comes in
/// on a <see cref="TickMsg"/>, data on a <see cref="SnapshotMsg"/>, so replaying the same messages gives the same screens.
/// What the world must do (ask Hetzner) leaves as a <see cref="TuiEffect"/>.
/// </summary>
internal static partial class TuiUpdate
{
    private const int MaxLog = 400;
    private static readonly TimeSpan NoticeFor = TimeSpan.FromSeconds(3);

    /// <summary>Hetzner is asked at the start and then this often; each question can take its 15 seconds, so it never waits on the loop.</summary>
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);

    public static TuiStep Update(TuiModel m, TuiMsg msg)
    {
        var step = msg switch
        {
            TickMsg tick => OnTick(m, tick),
            KeyMsg key => OnKey(m, key.Key, key.MoreBuffered),
            SnapshotMsg snapshot => TuiStep.Of(OnSnapshot(m, snapshot.Snapshot)),
            WorkerCheckedMsg checkedMsg => TuiStep.Of(OnWorkerChecked(m, checkedMsg)),
            ActionDoneMsg done => OnActionDone(m, done),
            QueueAdditionDoneMsg added => OnQueueAdditionDone(m, added),
            TuiLogMsg log => TuiStep.Of(AppendLines(m, [new LogLine(log.Kind, log.Text)])),
            WorkerOptionsMsg options => OnWorkerOptions(m, options),
            PreparedMsg prepared => TuiStep.Of(OnPrepared(m, prepared)),
            FinaliseStartedMsg started => TuiStep.Of(OnFinaliseStarted(m, started)),
            FinaliseEndedMsg ended => TuiStep.Of(OnFinaliseEnded(m, ended)),
            CostReadMsg cost => TuiStep.Of(OnCostRead(m, cost)),
            _ => TuiStep.Of(m)
        };
        return step with { Model = KeepSelectionsValid(step.Model) };
    }

    // ---- ticks and snapshots ----------------------------------------------------------------

    private static TuiStep OnTick(TuiModel m, TickMsg tick)
    {
        m = m with { Now = tick.Now, Width = tick.Width, Height = tick.Height };
        if (m.Notice is { } notice && tick.Now >= notice.Until) m = m with { Notice = null };
        var hetzner = AskHetznerWhenDue(m);
        var cost = ReadCostWhenDue(hetzner.Model);
        return new TuiStep(cost.Model, hetzner.Effects.AddRange(cost.Effects));
    }

    private static TuiStep AskHetznerWhenDue(TuiModel m)
    {
        var worker = m.Worker;
        if (worker is not { Managed: true, CheckInFlight: false }) return TuiStep.Of(m);
        if (worker.CheckAskedAt is { } asked && m.Now - asked < CheckEvery) return TuiStep.Of(m);
        return AskHetzner(m);
    }

    private static TuiStep AskHetzner(TuiModel m) =>
        new(m with { Worker = m.Worker with { CheckInFlight = true, CheckAskedAt = m.Now } }, [new RefreshWorkerCheck(m.Worker.Generation)]);

    private static TuiModel OnSnapshot(TuiModel m, TuiSnapshot snapshot)
    {
        var before = m.Ordered();
        var beforeReady = m.Ready();
        m = m with
        {
            Loaded = m.Loaded || snapshot.Open is not null,
            Open = snapshot.Open ?? m.Open,
            Failed = snapshot.Failed ?? m.Failed,
            ReadProblem = snapshot.Problems.Count == 0 ? null : snapshot.Problems.Count == 1 ? snapshot.Problems[0] : $"{snapshot.Problems[0]} (and {snapshot.Problems.Count - 1} more)"
        };
        m = m with
        {
            QueueSelection = Follow(before, m.QueueSelection, m.Ordered()),
            ReviewSelection = Follow(beforeReady, m.ReviewSelection, m.Ready())
        };
        if (snapshot.Run is { } run) m = m with { Run = run.Holder, Requests = run.Requests };
        if (snapshot.Worker is { } worker) m = ApplyWorker(m, worker);
        if (snapshot.Events is { } events) m = ApplyEvents(m, events);
        return m;
    }

    /// <summary>
    /// The cursor stays on the same job while the rows move under it. When its video is gone it goes to the next one that is
    /// still there, else the one before it.
    /// </summary>
    private static Guid? Follow(List<Video> before, Guid? selected, List<Video> after)
    {
        if (after.Count == 0) return null;
        if (after.Any(video => video.Id == selected)) return selected;
        var at = before.FindIndex(video => video.Id == selected);
        if (at < 0) return after[0].Id;
        var neighbours = before.Skip(at + 1).Concat(before.Take(at).Reverse());
        return neighbours.FirstOrDefault(video => after.Any(kept => kept.Id == video.Id))?.Id ?? after[0].Id;
    }

    private static TuiModel ApplyWorker(TuiModel m, WorkerSection section)
    {
        var before = m.Worker;
        var next = before with { Managed = section.Managed, Lifecycle = section.Lifecycle, Recorded = section.Recorded };
        // A worker made or deleted while a question was out makes its answer about another server: forget it, drop the answer
        // when it comes (WorkerCheckedMsg carries the generation it was asked in), and ask again at the next tick.
        var otherWorker = before.Managed && section.Managed && before.Recorded?.ServerId != section.Recorded?.ServerId;
        if (otherWorker || !section.Managed)
        {
            next = next with { Generation = before.Generation + 1, Check = null, CheckInFlight = false, CheckAskedAt = null };
        }
        return m with { Worker = next };
    }

    private static TuiModel OnWorkerChecked(TuiModel m, WorkerCheckedMsg answer) =>
        answer.Generation != m.Worker.Generation ? m : m with { Worker = m.Worker with { Check = answer.Check, CheckInFlight = false } };

    // ---- the event file ---------------------------------------------------------------------

    /// <summary>
    /// The file holds every event of the current or last run and is reread whole, so the model keeps how many it has shown and
    /// turns only the later ones into lines. A shorter file, or a different first event (another process, another start
    /// time), is another run: the log starts again. A reopened TUI therefore catches up on a run it never saw.
    /// </summary>
    private static TuiModel ApplyEvents(TuiModel m, IReadOnlyList<RunEvent> events)
    {
        var started = events.Count > 0 && events[0].Kind == RunEventKind.RunStarted ? events[0] : null;
        var activity = m.Activity;
        var log = m.Log;
        var scroll = m.LogScroll;
        if (events.Count < activity.EventsSeen || !SameRun(started, activity.Started))
        {
            activity = ActivityState.None with { Started = started };
            log = [];
            scroll = 0;
        }

        var added = new List<LogEntry>();
        for (var index = activity.EventsSeen; index < events.Count; index++)
        {
            var effect = RunEventWords.Of(events[index], m.Worker.Managed);
            if (effect.WorkerAnswered)
            {
                activity = activity with { LastAnswerAt = events[index].At };
                continue;
            }
            added.AddRange(effect.Lines);
            if (effect.Phase is { } phase) activity = activity with { Phase = phase };
            if (effect.SetProgress) activity = activity with { ProgressText = effect.ProgressText, ProgressPercent = effect.ProgressPercent };
            activity = activity with { LastEventAt = events[index].At };
            // Only before conversion starts is the run waiting for the worker: the delete at the end reports progress too.
            if (effect.WaitingForWorker is { } waiting) activity = activity with { WaitingForWorker = waiting && activity.Phase == RunPhase.Preparing };
        }
        activity = activity with { EventsSeen = events.Count };

        (log, scroll) = AppendLog(log, scroll, added);
        return m with { Activity = activity, Log = log, LogScroll = scroll };
    }

    private static bool SameRun(RunEvent? a, RunEvent? b) =>
        a is null || b is null ? a is null && b is null : a.Count == b.Count && a.At == b.At;

    // ---- keys -------------------------------------------------------------------------------

    private static TuiStep OnKey(TuiModel m, ConsoleKeyInfo key, bool moreBuffered)
    {
        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control) && !key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            // Ctrl+C twice always gets out: the second press lands in the quit dialog.
            return m.Busy.IsEmpty || m.Dialog is TuiConfirm { OnYes: QuitNow } ? TuiStep.Of(m with { Quit = true }) : TuiStep.Of(ConfirmQuit(m));
        }

        // Any key closes the guide, and does nothing else.
        if (m.Help) return TuiStep.Of(m with { Help = false });

        switch (m.Dialog)
        {
            case TuiConfirm confirm:
                // A question being answered stays: ? is for the screen, not for the dialog.
                if (TuiKeys.Anywhere.Guide.Is(key.KeyChar)) return TuiStep.Of(m);
                // Only an explicit y confirms; anything else, Enter included, is the [y/N] default.
                var yes = key.Key == ConsoleKey.Y && key.Modifiers == 0;
                m = m with { Dialog = null };
                if (!yes) return TuiStep.Of(m);
                return confirm.OnYes is QuitNow ? TuiStep.Of(m with { Quit = true }) : Emit(m, confirm.OnYes);
            case TuiChoice choice:
                return OnChoiceKey(m, choice, key.Key);
            case TuiTextInput input:
                return OnQueueInputKey(m, input, key, moreBuffered);
        }

        // Addition has its own cancellation source. One Esc must never also stop a finalise underneath it.
        if (key.Key == ConsoleKey.Escape && m.Busy.ContainsKey(AddVideos.Key)) return CancelAddition(m);
        // Console pastes may end in CRLF: its trailing LF must not become a screen action after Enter submits a field.
        if (char.IsControl(key.KeyChar) && key.Key is not (ConsoleKey.Tab or ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.PageUp or ConsoleKey.PageDown or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.Escape)) return TuiStep.Of(m);

        // The cost page takes the keys after the dialogs, which an effect may open over it (the quit question among them).
        if (m.Cost is { } page) return CostKey(m, page, key);
        if (TuiKeys.Anywhere.Cost.Is(key.KeyChar)) return OpenCost(m);

        if (TuiKeys.Anywhere.Guide.Is(key.KeyChar)) return TuiStep.Of(m with { Help = true });
        // The dialogs above take Esc as cancel; with none open it is the finalise's stop, from every pane like x.
        if (key.Key == ConsoleKey.Escape) return StopFinaliseKey(m);

        switch (key.Key)
        {
            case ConsoleKey.Tab:
                return TuiStep.Of(m with { Focus = Cycle(m.Focus, key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1) });
            case ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.PageUp or ConsoleKey.PageDown or ConsoleKey.Home or ConsoleKey.End:
                return TuiStep.Of(Navigate(m, key.Key));
        }

        if (!TuiKeys.Anywhere.Quit.Is(key.KeyChar)) return ActionKey(m, key.KeyChar);
        return m.Busy.IsEmpty ? TuiStep.Of(m with { Quit = true }) : TuiStep.Of(ConfirmQuit(m));
    }

    /// <summary>
    /// Quitting ends the process, so an action still going (a worker created between Hetzner's request and the state file, a
    /// finalise mid-install) would stop partway. Reads (the snapshot, Hetzner's check) hold no key and never ask. A queue
    /// addition is the exception: quitting cancels it and waits for its cleanup (<see cref="TuiEffects.StopAdditionAsync"/>),
    /// so it ends as Esc would leave it, and the question says so instead of warning (VOPT-195).
    /// </summary>
    private static TuiModel ConfirmQuit(TuiModel m)
    {
        var adding = m.Busy.TryGetValue(AddVideos.Key, out var addText) ? addText.TrimEnd('.') : null;
        var others = m.Busy.Where(pair => pair.Key != AddVideos.Key).Select(pair => pair.Value.TrimEnd('.')).Order(StringComparer.Ordinal).ToArray();
        var count = others.Length + (adding is null ? 0 : 1);
        var question = count == 1 ? $"{adding ?? others[0]} is still going." : $"{count} actions are still going.";
        var detail = others.Length switch
        {
            0 => "Quitting stops it. Videos already queued stay queued.",
            1 when adding is null => "Quitting now stops it partway. It's better to wait for it to finish.",
            1 => $"{others[0]}. Quitting now stops it partway. It's better to wait for it to finish.",
            _ => $"{string.Join("; ", others)}. Quitting now stops them partway. It's better to wait for them to finish."
        };
        if (adding is not null && others.Length > 0) detail += $" {adding} stops too, and videos already queued stay queued.";
        // A long batch need not be waited out: Esc ends it cleanly after the video it is on. With an addition out, Esc
        // cancels that first, so the sentence would be wrong.
        else if (adding is null && m.Finalising is { CanStop: true }) detail += " " + EscStopsTheFinalise;
        return m with { Dialog = new TuiConfirm(question, detail, new QuitNow()) };
    }

    private static Pane Cycle(Pane pane, int by) => (Pane)(((int)pane + by + 4) % 4);

    private static TuiModel Navigate(TuiModel m, ConsoleKey key)
    {
        switch (m.Focus)
        {
            case Pane.Queue:
                return m with { QueueSelection = Move(m.Ordered(), m.QueueSelection, key) };
            case Pane.Review:
                return m with { ReviewSelection = Move(m.Ready(), m.ReviewSelection, key) };
            case Pane.Activity:
                var last = Math.Max(0, m.Log.Count - 1);
                var scroll = key switch
                {
                    ConsoleKey.UpArrow => m.LogScroll + 1,
                    ConsoleKey.DownArrow => m.LogScroll - 1,
                    ConsoleKey.PageUp => m.LogScroll + 10,
                    ConsoleKey.PageDown => m.LogScroll - 10,
                    ConsoleKey.Home => last,
                    _ => 0
                };
                return m with { LogScroll = Math.Clamp(scroll, 0, last) };
            default:
                return m;
        }
    }

    private static Guid? Move(List<Video> rows, Guid? selected, ConsoleKey key)
    {
        if (rows.Count == 0) return null;
        var at = Math.Max(0, rows.FindIndex(video => video.Id == selected));
        at = key switch
        {
            ConsoleKey.UpArrow => at - 1,
            ConsoleKey.DownArrow => at + 1,
            ConsoleKey.PageUp => at - 5,
            ConsoleKey.PageDown => at + 5,
            ConsoleKey.Home => 0,
            _ => rows.Count - 1
        };
        return rows[Math.Clamp(at, 0, rows.Count - 1)].Id;
    }

    private static TuiModel Tell(TuiModel m, string text) => m with { Notice = new Notice(text, m.Now + NoticeFor) };

    private static TuiStep Told(TuiModel m, string text) => TuiStep.Of(Tell(m, text));

    // A key opens its dialog directly: OnKey hands every key to an open dialog first, so a key handler never runs under one.

    private static TuiStep Confirming(TuiModel m, string question, string detail, TuiEffect onYes) => Asking(m, new TuiConfirm(question, detail, onYes));

    private static TuiStep Asking(TuiModel m, TuiDialog dialog) => TuiStep.Of(m with { Dialog = dialog });

    /// <summary>
    /// An effect's answer opens its dialog only when none is open. The user may have opened another one while the effect was
    /// out, and swapping or covering it would hand a y meant for that question to this one (VOPT-141): a reject or a finalise
    /// they never saw. The answer is dropped instead, and <paramref name="dropped"/> names the key that asks for it again. The
    /// key guide doesn't count, since any key closes it.
    /// </summary>
    private static TuiModel OpenAnswer(TuiModel m, TuiDialog dialog, string dropped) =>
        m.Dialog is null ? m with { Dialog = dialog } : Tell(m, dropped);

    /// <summary>
    /// Starts an effect and marks what it holds: a second one with the same key is refused with the first one's text, so two
    /// presses never start the same thing twice.
    /// </summary>
    private static TuiStep Emit(TuiModel m, TuiEffect effect)
    {
        if (effect.BusyKey is { } key)
        {
            if (m.Busy.TryGetValue(key, out var working)) return Told(m, working);
            m = m with { Busy = m.Busy.SetItem(key, effect.BusyText ?? "Working.") };
        }
        // Only an accepted finalise starts a batch: a refused one leaves the batch that is out as it is.
        if (effect is FinaliseVideos finalise) m = m with { Finalising = new FinaliseBatch([.. finalise.Ids], null, 0, 0, 0) };
        return new TuiStep(m, [effect]);
    }

    private static TuiModel AppendLines(TuiModel m, IEnumerable<LogLine> lines)
    {
        var (log, scroll) = AppendLog(m.Log, m.LogScroll, [.. lines.Select(line => new LogEntry(m.Now, line.Kind, line.Text))]);
        return m with { Log = log, LogScroll = scroll };
    }

    private static (ImmutableList<LogEntry> Log, int Scroll) AppendLog(ImmutableList<LogEntry> log, int scroll, List<LogEntry> added)
    {
        if (added.Count == 0) return (log, scroll);
        log = log.AddRange(added);
        if (log.Count > MaxLog) log = log.RemoveRange(0, log.Count - MaxLog);
        // A pane scrolled back keeps showing the same lines while new ones arrive, like a terminal does.
        if (scroll > 0) scroll = Math.Min(scroll + added.Count, Math.Max(0, log.Count - 1));
        return (log, scroll);
    }

    /// <summary>A cursor on no video, or on one that is gone, goes to the first row so a list that has rows always has a selection.</summary>
    private static TuiModel KeepSelectionsValid(TuiModel m)
    {
        var queue = m.Ordered();
        var ready = m.Ready();
        return m with
        {
            QueueSelection = queue.Any(video => video.Id == m.QueueSelection) ? m.QueueSelection : queue.FirstOrDefault()?.Id,
            ReviewSelection = ready.Any(video => video.Id == m.ReviewSelection) ? m.ReviewSelection : ready.FirstOrDefault()?.Id
        };
    }
}
