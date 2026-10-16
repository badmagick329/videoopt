using System.Diagnostics;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>Opens a file in Windows' default app. A seam so a test records the path instead of starting a player.</summary>
internal interface IFileOpener
{
    void Open(string path);
}

internal sealed class ShellFileOpener : IFileOpener
{
    public void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
}
