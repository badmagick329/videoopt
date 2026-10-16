using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// How to stop the run that has a video, for the notices that refuse something because a video is converting. A background run
/// stops through the requests row, so the keys do it; a foreground run never reads that row, and only Ctrl+C in its window stops it.
/// </summary>
internal static class StopWords
{
    public static string HowToStop(LiveRunHolder? run) => run switch
    {
        null => "Wait for the run to stop.",
        { Command: LockHolders.BackgroundRun } => "Press x to stop the run after this video, or X to stop it now.",
        _ => "Stop the run with Ctrl+C in its window."
    };
}
