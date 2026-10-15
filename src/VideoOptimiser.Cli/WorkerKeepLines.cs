namespace VideoOptimiser.Cli;

/// <summary>
/// What a command says when it ends with the managed worker still up. It lives at the Cli root because a run and
/// <c>worker create</c> (stopped with Ctrl+C after the server exists) both end this way, and neither slice may reach into the
/// other. Changed wording goes through the humanizer gate (docs index).
/// </summary>
internal static class WorkerKeepLines
{
    public static IReadOnlyList<string> For(string worker, CliCommand command) =>
    [
        $"The worker {worker} stays up, billed hourly, until it is deleted.",
        $"To delete it: {command.Hint("worker delete")}"
    ];
}
