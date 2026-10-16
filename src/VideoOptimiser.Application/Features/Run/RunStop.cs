namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// How a run is told to stop, from Ctrl+C or from a stop request (VOPT-54 step 3, VOPT-110). The first press while a video
/// converts stops after that video, so its CRF search, encode and download are kept; a second press, or a first one with
/// nothing converting, stops now, and the video resumes from its stage next run. A press while the worker is being deleted
/// stops nothing: the delete would be cut off and the worker left billing. Child processes run with their own hidden console
/// (<c>CreateNoWindow</c>), so a press never reaches ab-av1 or ffprobe.
/// </summary>
/// <remarks>
/// One per command, registered as a service so a test can press it. <see cref="Press"/> runs on the console's signal thread;
/// a background run has no console, so its request poll calls <see cref="Press"/> for <c>run stop</c> and <see cref="StopNow"/>
/// for <c>run stop --now</c>, which is the state machine Ctrl+C already is.
/// </remarks>
public sealed class RunStop : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _now = new();
    private readonly CancellationTokenSource _afterThisVideo = new();
    private Action? _onStopAfterThisVideo;
    private Action? _onPressWhileDeleting;

    /// <summary>Cancelled by a stop now: every stage observes it.</summary>
    public CancellationToken Now => _now.Token;

    /// <summary>Cancelled by a stop after this video: the run checks it only between videos.</summary>
    public CancellationToken AfterThisVideo => _afterThisVideo.Token;

    public void Press()
    {
        // The callbacks run under the lock, so a press as the conversion ends never prints "Stopping after this video" once
        // the run has moved on to its summary: the scope's exit waits for it, or it sees the scope gone.
        lock (_gate)
        {
            if (_onPressWhileDeleting is not null)
            {
                _onPressWhileDeleting();
                return;
            }
            if (_onStopAfterThisVideo is not null && !_afterThisVideo.IsCancellationRequested)
            {
                _afterThisVideo.Cancel();
                _onStopAfterThisVideo();
                return;
            }
        }

        _now.Cancel();
    }

    /// <summary>
    /// What a second press does, whether or not a first came: stops now, except while the worker is being deleted, when it does
    /// nothing. A stop request for "now" must never cut off the delete, and it needs no message for the person who sent it.
    /// </summary>
    public void StopNow()
    {
        lock (_gate)
        {
            if (_onPressWhileDeleting is not null) return;
        }

        _now.Cancel();
    }

    /// <summary>While the returned scope lasts, a first press stops after this video and calls <paramref name="onStop"/>.</summary>
    public IDisposable WhileConverting(Action onStop) => Scope(() => _onStopAfterThisVideo = onStop, () => _onStopAfterThisVideo = null);

    /// <summary>While the returned scope lasts, a press only calls <paramref name="onPress"/>.</summary>
    public IDisposable WhileDeleting(Action onPress) => Scope(() => _onPressWhileDeleting = onPress, () => _onPressWhileDeleting = null);

    private ScopeExit Scope(Action enter, Action exit)
    {
        lock (_gate) enter();
        return new ScopeExit(() => { lock (_gate) exit(); });
    }

    public void Dispose()
    {
        _now.Dispose();
        _afterThisVideo.Dispose();
    }

    private sealed class ScopeExit(Action exit) : IDisposable
    {
        public void Dispose() => exit();
    }
}
