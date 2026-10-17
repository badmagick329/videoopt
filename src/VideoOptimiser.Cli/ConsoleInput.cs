namespace VideoOptimiser.Cli;

/// <summary>What the user types, behind an interface so a test can answer the worker question at a chosen moment.</summary>
internal interface IConsoleInput
{
    /// <summary>
    /// False when input is redirected. The worker question is then not asked: a script that pipes "y" means it for the
    /// creation prompt, which reads piped input on purpose, not for deleting the worker.
    /// </summary>
    bool IsInteractive { get; }

    /// <summary>The line typed, or null for no answer: input ended, or Ctrl+C ended the read.</summary>
    string? ReadLine();
}

internal sealed class ConsoleInput : IConsoleInput
{
    public bool IsInteractive
    {
        get
        {
            try { return !CliConsole.IsInputRedirected; }
            catch (IOException) { return false; }
        }
    }

    public string? ReadLine()
    {
        try { return CliConsole.ReadLine(); }
        catch (IOException) { return null; }
    }
}
