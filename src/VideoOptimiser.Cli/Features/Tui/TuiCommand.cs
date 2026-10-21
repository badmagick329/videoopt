using System.Text;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// The hidden <c>tui --snapshot [--size WxH]</c>: reads the real data once and prints the screen as one plain frame, so a person
/// or an agent can check what the TUI shows from a shell, with no terminal to drive. It asks Hetzner about the managed worker
/// once, as the TUI does at its start.
/// </summary>
internal sealed class TuiCommand(TuiSnapshotReader reader, TimeProvider time)
{
    public async Task<int> SnapshotAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        var settings = configuration.Settings;
        var size = command.Size ?? FrameSize.Default;
        var model = TuiModel.Initial(time.GetUtcNow(), size.Width, size.Height, Path.GetFileName(settings.Database.Path));

        model = TuiUpdate.Update(model, new SnapshotMsg(await reader.ReadAsync(settings, cancellationToken))).Model;
        if (model.Worker.Managed)
        {
            var check = await reader.CheckWorkerAsync(settings, cancellationToken);
            model = TuiUpdate.Update(model, new WorkerCheckedMsg(model.Worker.Generation, check)).Model;
        }

        foreach (var key in command.SnapshotKeys ?? []) model = TuiUpdate.Update(model, key).Model;

        using var renderer = new FrameRenderer();
        var frame = renderer.Frame(model)!;
        // The frame's box glyphs are not in the console's default code page when stdout is a pipe or a file.
        // A replaced writer has its own encoding, and the code page is process-wide, so parallel runs would race to set it.
        try
        {
            if (!CliConsole.IsReplaced) Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // No console to set a code page on: the writer's own encoding stands.
        }
        foreach (var row in FrameRenderer.Plain(frame).Split('\n')) CliConsole.Out.WriteLine(row.TrimEnd());
        return (int)ExitCode.Success;
    }
}
