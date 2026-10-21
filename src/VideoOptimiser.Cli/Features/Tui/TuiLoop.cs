using System.Threading.Channels;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// The Bubble Tea loop: one channel in, Update, then View drawn through the renderer. Keys, ticks, snapshots and the answers to
/// effects are all messages on that one channel, so the loop thread is the only thing that touches the model.
/// </summary>
internal static class TuiLoop
{
    /// <summary>
    /// Update on every waiting message, then draw once, then start what the updates asked for. Draining first keeps a held key
    /// from queueing up frames. Returns the last model, so a test can see where the loop ended.
    /// </summary>
    /// <param name="run">Starts one effect off this thread; it returns at once and posts the effect's result as a message.</param>
    public static async Task<TuiModel> PumpAsync(ChannelReader<TuiMsg> reader, TuiModel model, Action<string> write, Action<TuiEffect> run, CancellationToken cancellationToken)
    {
        using var renderer = new FrameRenderer();
        if (renderer.Frame(model) is { } first) write(first);
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                var effects = new List<TuiEffect>();
                while (reader.TryRead(out var msg))
                {
                    var step = TuiUpdate.Update(model, msg);
                    model = step.Model;
                    effects.AddRange(step.Effects);
                }
                if (model.Quit) return model;
                if (renderer.Frame(model) is { } frame) write(frame);
                foreach (var effect in effects) run(effect);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+Break: leave the loop and let the caller restore the terminal.
        }
        return model;
    }

    /// <summary>
    /// Keys are read on their own thread, because ReadKey blocks; the loop only ever sees messages. The thread is a background
    /// one: it is left blocked in ReadKey when the TUI quits, and the process ends without waiting for it.
    /// </summary>
    public static void StartKeys(ChannelWriter<TuiMsg> into)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    // A paste arrives as one burst, so an Enter inside it has the rest of the paste waiting behind it.
                    into.TryWrite(new KeyMsg(key, key.Key == ConsoleKey.Enter && Console.KeyAvailable));
                }
            }
            catch (InvalidOperationException)
            {
                // Input was redirected away (ReadKey and KeyAvailable both throw then): there are no more keys.
            }
            catch (IOException)
            {
                // The console is gone.
            }
        })
        { IsBackground = true, Name = "tui-keys" };
        thread.Start();
    }

    /// <summary>About four ticks a second. Each carries the clock and the window size, so a resize is just another message.</summary>
    public static void StartTicker(ChannelWriter<TuiMsg> into, TimeProvider time, Func<(int Width, int Height)> size, CancellationToken cancellationToken) => _ = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        into.TryWrite(MakeTick(time, size));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) into.TryWrite(MakeTick(time, size));
        }
        catch (OperationCanceledException)
        {
            // The TUI is closing.
        }
    }, CancellationToken.None);

    private static TickMsg MakeTick(TimeProvider time, Func<(int Width, int Height)> size)
    {
        var (width, height) = size();
        return new TickMsg(time.GetUtcNow(), width, height);
    }

    /// <summary>
    /// Reads a snapshot now and then about once a second, off the loop thread, and posts it. A read that throws anyway
    /// (the reader catches each section's failure itself) becomes a snapshot that says so, so the feed never ends.
    /// </summary>
    public static void StartFeed(ChannelWriter<TuiMsg> into, Func<CancellationToken, Task<TuiSnapshot>> read, CancellationToken cancellationToken) => _ = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                TuiSnapshot snapshot;
                try
                {
                    snapshot = await read(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    snapshot = new TuiSnapshot(null, null, null, null, null, [$"Couldn't read the data: {exception.GetBaseException().Message}"]);
                }
                into.TryWrite(new SnapshotMsg(snapshot));
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // The TUI is closing.
        }
    }, CancellationToken.None);
}
