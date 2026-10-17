using System.Threading.Channels;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// Runs the effects <see cref="TuiUpdate"/> asks for, each off the loop thread, and posts what came of it back as a message.
/// Nothing here decides anything: the next state is Update's, from the message. An action that fails is a log line in the error
/// colour and never a crash; a video another command changed under it says so and the list refreshes.
/// </summary>
/// <remarks>
/// Hetzner's check writes the server it finds into the settings it is given (<c>processing.remoteSsh</c>'s host and key, in
/// memory only), as it does for <c>status</c>, which passes the loaded settings too; the TUI does the same.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001", Justification = "Each finalise stop source is disposed when its batch ends; nothing else is owned.")]
internal sealed class TuiEffects(TuiSnapshotReader reader, TuiActions actions, LoadedConfiguration configuration, ChannelWriter<TuiMsg> into)
{
    private AppSettings Settings => configuration.Settings;

    /// <summary>
    /// Esc's stop for the finalise batch that is out; null with none. Kept apart from the TUI's closing token, which cuts a
    /// video off: this one is only read between videos. Made on the loop thread in <see cref="Run"/>, before the batch's task
    /// starts, so an Esc handled right after the confirmation always finds it.
    /// </summary>
    private CancellationTokenSource? _finaliseStop;

    /// <summary>Starts <paramref name="effect"/> and returns at once.</summary>
    public void Run(TuiEffect effect, CancellationToken cancellationToken)
    {
        if (effect is StopFinalise)
        {
            try
            {
                _finaliseStop?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The batch ended between the key and here: there is nothing left to stop.
            }
            return;
        }
        // The finalise busy key lets one batch out at a time, so one source is enough.
        var stop = effect is FinaliseVideos ? _finaliseStop = new CancellationTokenSource() : null;
        _ = Task.Run(() => RunAsync(effect, stop, cancellationToken), CancellationToken.None);
    }

    private async Task RunAsync(TuiEffect effect, CancellationTokenSource? stop, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(effect, stop?.Token ?? CancellationToken.None, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The TUI is closing.
        }
        catch (JobChangedException)
        {
            Finish(effect, ActionResult.Say(LogKind.Warning, "Another command changed that video just now. The list is refreshing; try again."));
        }
        catch (Exception exception)
        {
            Finish(effect, ActionResult.Say(LogKind.Error, $"That didn't work: {RunEventWords.Clean(exception.GetBaseException().Message.Split('\n', 2)[0])}"));
        }
        finally
        {
            if (stop is not null)
            {
                Interlocked.CompareExchange(ref _finaliseStop, null, stop);
                stop.Dispose();
            }
        }
    }

    /// <summary>Frees what the effect held and hands its words to Update. An effect that holds nothing frees an empty key.</summary>
    private void Finish(TuiEffect effect, ActionResult result) => into.TryWrite(new ActionDoneMsg(effect.BusyKey ?? string.Empty, result));

    private void Log(LogKind kind, string text) => into.TryWrite(new TuiLogMsg(kind, text));

    private async Task ExecuteAsync(TuiEffect effect, CancellationToken stopAfterVideo, CancellationToken cancellationToken)
    {
        switch (effect)
        {
            case RefreshWorkerCheck refresh:
                into.TryWrite(new WorkerCheckedMsg(refresh.Generation, await reader.CheckWorkerAsync(Settings, cancellationToken)));
                break;
            case RefreshSnapshot:
                into.TryWrite(new SnapshotMsg(await reader.ReadAsync(Settings, cancellationToken)));
                break;
            case ReadCost:
                into.TryWrite(await actions.ReadCostAsync(Settings, cancellationToken));
                break;
            case QueueActionEffect queue:
                Finish(effect, await actions.QueueAsync(queue, Settings, cancellationToken));
                break;
            case DismissAllFailures:
                Finish(effect, await actions.DismissAllAsync(Settings, cancellationToken));
                break;
            case LoadWorkerOptions load:
                into.TryWrite(await actions.LoadWorkerOptionsAsync(load.Purpose, Settings, cancellationToken));
                break;
            case CreateWorker create:
                Finish(effect, await actions.CreateWorkerAsync(create.Option, Settings, Log, cancellationToken));
                break;
            case DeleteWorker:
                Finish(effect, await actions.DeleteWorkerAsync(Settings, Log, cancellationToken));
                break;
            case StartRun start:
                Finish(effect, await actions.StartRunAsync(start.Plan, Settings, configuration.Path, Log, cancellationToken));
                break;
            case StopRun stop:
                Finish(effect, await actions.StopAsync(stop.Now, Settings, cancellationToken));
                break;
            case SetWorkerFate fate:
                Finish(effect, await actions.SetWorkerFateAsync(fate.Fate, Settings, cancellationToken));
                break;
            case OpenFile open:
                Finish(effect, await actions.OpenAsync(open, Settings, cancellationToken));
                break;
            case PrepareFinalise prepare:
                into.TryWrite(await actions.PrepareFinaliseAsync(prepare, Settings, cancellationToken));
                break;
            case FinaliseVideos finalise:
                Finish(effect, await actions.FinaliseAsync(finalise, Settings, msg => into.TryWrite(msg), stopAfterVideo, cancellationToken));
                break;
        }
    }
}
