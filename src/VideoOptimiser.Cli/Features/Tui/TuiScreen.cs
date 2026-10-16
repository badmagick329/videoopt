using System.Threading.Channels;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>The full-screen TUI a bare <c>video-optimiser</c> opens, behind a seam so a test of the bare command swaps it for a fake.</summary>
internal interface ITuiScreen
{
    /// <summary>Both stdin and stdout are a terminal: redirected, the bare command prints the status card instead.</summary>
    bool IsAvailable { get; }

    /// <summary>Runs until the user quits. Quitting never stops a run: a run holds its own process and lock, not this one.</summary>
    Task<int> RunAsync(LoadedConfiguration configuration, CancellationToken cancellationToken);
}

internal sealed class TuiScreen(TuiSnapshotReader reader, TuiActions actions, TimeProvider time) : ITuiScreen
{
    public bool IsAvailable => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public async Task<int> RunAsync(LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        var settings = configuration.Settings;
        var session = new TerminalSession();
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Ctrl+C arrives as a key (TreatControlCAsInput); Ctrl+Break is the one signal left, and it leaves the loop too.
        ConsoleCancelEventHandler onBreak = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            closing.Cancel();
        };
        Console.CancelKeyPress += onBreak;
        try
        {
            session.Enter();
            var channel = Channel.CreateUnbounded<TuiMsg>(new UnboundedChannelOptions { SingleReader = true });
            var (width, height) = WindowSize();
            var model = TuiModel.Initial(time.GetUtcNow(), width, height, Path.GetFileName(settings.Database.Path));
            var effects = new TuiEffects(reader, actions, configuration, channel.Writer);

            TuiLoop.StartKeys(channel.Writer);
            TuiLoop.StartTicker(channel.Writer, time, WindowSize, closing.Token);
            TuiLoop.StartFeed(channel.Writer, token => reader.ReadAsync(settings, token), closing.Token);

            await TuiLoop.PumpAsync(channel.Reader, model, session.Write, effect => effects.Run(effect, closing.Token), closing.Token);
            return (int)ExitCode.Success;
        }
        finally
        {
            await closing.CancelAsync();
            Console.CancelKeyPress -= onBreak;
            session.Restore();
        }
    }

    private static (int Width, int Height) WindowSize()
    {
        try
        {
            return (Console.WindowWidth, Console.WindowHeight);
        }
        catch (IOException)
        {
            return (80, 24);
        }
    }
}
