using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Application.Tools;

public enum AbAv1Stage
{
    CrfSearch,
    Encode
}

public interface IAbAv1ProgressTranslator
{
    /// <summary>
    /// Wraps <paramref name="progress"/> so the stage's raw ab-av1 lines arrive as the job's progress lines. Each stage gets
    /// a fresh wrapper: its per-stage state (predictions, progress throttle, timing) must not carry over.
    /// </summary>
    IProgress<JobProgress> Wrap(IProgress<JobProgress> progress, AbAv1Stage stage, QualitySettings quality);
}
