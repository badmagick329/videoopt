using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Application.Tools;

public enum AbAv1Stage
{
    CrfSearch,
    Encode
}

/// <summary>
/// Progress for a remote stage that can be told it was already running when this command reached it. Such a stage's earlier
/// output is never replayed, so the progress it shows and the times it measures start at the reattach: a stage that began
/// before it has no duration this command could report.
/// </summary>
public interface IReattachableProgress : IProgress<JobProgress>
{
    void StageWasAlreadyRunning();
}

public interface IAbAv1ProgressTranslator
{
    /// <summary>
    /// Wraps <paramref name="progress"/> so the stage's raw ab-av1 lines arrive as the job's progress lines. Each stage gets
    /// a fresh wrapper: its per-stage state (predictions, progress throttle, timing) must not carry over.
    /// </summary>
    IReattachableProgress Wrap(IProgress<JobProgress> progress, AbAv1Stage stage, QualitySettings quality);
}
