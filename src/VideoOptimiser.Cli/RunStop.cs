namespace VideoOptimiser.Cli;

/// <summary>
/// Ctrl+C (VOPT-54 step 3). The first press while a video converts stops after that video, so its CRF search, encode and
/// download are kept; a second press, or a first one with nothing converting, stops now, and the video resumes from its stage
/// next run. A press while the worker is being deleted stops nothing: the delete would be cut off and the worker left billing.
/// Child processes run with their own hidden console (<c>CreateNoWindow</c>), so a press never reaches ab-av1 or ffprobe.
/// </summary>
/// <remarks>
/// One per command, registered as a service so a test can press it; <see cref="Press"/> runs on the console's signal thread.
/// </remarks>
internal sealed class RunStop : IDisposable
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
        Action? notify;
        lock (_gate)
        {
            if (_onPressWhileDeleting is not null)
            {
                notify = _onPressWhileDeleting;
            }
            else if (_onStopAfterThisVideo is not null && !_afterThisVideo.IsCancellationRequested)
            {
                _afterThisVideo.Cancel();
                notify = _onStopAfterThisVideo;
            }
            else
            {
                notify = null;
            }
        }

        if (notify is null) _now.Cancel();
        else notify();
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
