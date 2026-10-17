using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;

namespace VideoOptimiser.Cli;

/// <summary>The yes/no questions more than one command asks: worker create, the run, and remote setup.</summary>
internal static class ConsolePrompts
{
    /// <summary>
    /// Reads the answer even when input is redirected, like <c>finalize</c>, so an agent or script approves by piping
    /// <c>'y'</c>; the worker question refuses redirected input (<see cref="IConsoleInput.IsInteractive"/>). No input at all declines.
    /// </summary>
    public static Task<bool> ConfirmWorkerCreationAsync(RemoteWorkerCreationQuote quote, CancellationToken cancellationToken) =>
        ConfirmAsync(quote.FormatPrompt(), cancellationToken);

    public static Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken)
    {
        CliConsole.Write(prompt);
        string? response;
        try { response = CliConsole.ReadLine(); }
        catch (IOException) { response = null; }
        // Ctrl+C at the prompt ends the read with no answer; report it as a cancellation, not a decline.
        cancellationToken.ThrowIfCancellationRequested();
        if (response is null) CliConsole.WriteLine();
        return Task.FromResult(WorkerEndPolicy.IsAffirmative(response));
    }
}
