using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// The detached runner's command line, after the exe. <c>background start</c> and the TUI's start action both build it here,
/// so the two ways of starting a background run cannot drift apart.
/// </summary>
internal static class BackgroundRunnerArguments
{
    /// <summary>The runner flag that forbids it to create a managed worker (<see cref="BackgroundRun.RunAsync"/>'s <c>mayCreateWorker</c>).</summary>
    public const string ExistingWorkerFlag = "--existing-worker";

    /// <summary>
    /// The full path of the configuration, since the runner's working directory is the launcher's but it must not depend on
    /// that, and a mode flag only when the launcher chose one.
    /// </summary>
    /// <param name="existingWorker">
    /// The launcher settled the worker before launch (the TUI's "Use X", or a server it created), so a worker missing by the
    /// time the runner needs it was deleted meanwhile, and the runner must end rather than create a server nobody chose
    /// (VOPT-154). False keeps <c>background start</c>'s behaviour: the runner creates one when none is up.
    /// </param>
    public static IReadOnlyList<string> For(string configurationPath, WorkerFateChoice fate, ProcessingModeOverride? mode, bool existingWorker)
    {
        List<string> arguments = ["background", "runner", "--worker-fate", fate == WorkerFateChoice.Keep ? "keep" : "delete", "--config", Path.GetFullPath(configurationPath)];
        if (mode is not null) arguments.Add(mode.Flag);
        if (existingWorker) arguments.Add(ExistingWorkerFlag);
        return arguments;
    }
}
