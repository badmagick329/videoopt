using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// The detached runner's command line, after the exe. <c>background start</c> and the TUI's start action both build it here,
/// so the two ways of starting a background run cannot drift apart.
/// </summary>
internal static class BackgroundRunnerArguments
{
    /// <summary>
    /// The full path of the configuration, since the runner's working directory is the launcher's but it must not depend on
    /// that, and a mode flag only when the launcher chose one.
    /// </summary>
    public static IReadOnlyList<string> For(string configurationPath, WorkerFateChoice fate, ProcessingModeOverride? mode)
    {
        List<string> arguments = ["background", "runner", "--worker-fate", fate == WorkerFateChoice.Keep ? "keep" : "delete", "--config", Path.GetFullPath(configurationPath)];
        if (mode is not null) arguments.Add(mode.Flag);
        return arguments;
    }
}
