using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// How to stop the run that has a video, for the notices that refuse something because a video is converting. Which run is
/// which is <see cref="LiveRunHolder.IsBackground"/>'s call: a background run stops through the requests row, so the keys do
/// it; a foreground run never reads that row, and only Ctrl+C in its window stops it.
/// </summary>
internal static class StopWords
{
    /// <param name="stopAfterVideoWorks">
    /// Whether a stop after this video also lets the refused action go: a drop takes the ready video it leaves, but a hold
    /// cannot, since a finished video is no longer waiting. Without it only <c>X</c> is offered, as the CLI offers only
    /// <c>run stop --now</c> (VOPT-170).
    /// </param>
    public static string HowToStop(LiveRunHolder? run, bool stopAfterVideoWorks) => run switch
    {
        null => "Wait for the run to stop.",
        { IsBackground: true } when stopAfterVideoWorks => "Press x to stop the run after this video, or X to stop it now.",
        { IsBackground: true } => "Press X to stop the run now.",
        _ => "Stop the run with Ctrl+C in its window."
    };
}
