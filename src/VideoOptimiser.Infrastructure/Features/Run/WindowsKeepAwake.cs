using System.Runtime.InteropServices;
using VideoOptimiser.Application.Features.Run;

namespace VideoOptimiser.Infrastructure.Features.Run;

/// <summary>
/// Keeps Windows from sleeping while a run works. <c>SetThreadExecutionState</c> belongs to the calling thread, and a run's
/// async continuations hop threads, so the request is made and withdrawn on one dedicated thread that lives as long as the
/// hold. <c>ES_CONTINUOUS</c> makes the request last; without it <c>ES_SYSTEM_REQUIRED</c> resets the idle timer once.
/// Best effort: a refused call is ignored, because keep-awake must never fail a run.
/// </summary>
public sealed class WindowsKeepAwake : IKeepAwake
{
    public IDisposable Hold() => new KeepAwakeHold();
}

internal sealed partial class KeepAwakeHold : IDisposable
{
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsContinuous = 0x80000000;
    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _thread;
    private int _disposed;

    public KeepAwakeHold()
    {
        using var requested = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _ = SetThreadExecutionState(EsContinuous | EsSystemRequired);
            requested.Set();
            _release.Wait();
            _ = SetThreadExecutionState(EsContinuous);
        })
        {
            IsBackground = true,
            Name = "keep-awake"
        };
        _thread.Start();
        // The request is in force when Hold returns, not some time after.
        requested.Wait();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _release.Set();
        _thread.Join();
        _release.Dispose();
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);
}
