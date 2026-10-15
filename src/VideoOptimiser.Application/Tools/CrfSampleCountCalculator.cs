namespace VideoOptimiser.Application.Tools;

public static class CrfSampleCountCalculator
{
    public static int Calculate(double? durationSeconds, int fallbackSampleCount)
    {
        if (durationSeconds is not > 0)
        {
            return fallbackSampleCount;
        }

        var minutes = durationSeconds.Value / 60d;
        return Math.Clamp((int)Math.Ceiling(minutes / 2), 2, 12);
    }

    /// <summary>
    /// Mirrors ab-av1's own rule (sample_encode.rs, 0.11.7): when the samples would cover at least 85% of the video, or the
    /// sample duration is zero, it encodes the whole video once instead of cutting samples. Progress text must say so, or a
    /// 7-second clip is reported as two 20-second samples.
    /// </summary>
    public static bool TestsWholeVideo(double? durationSeconds, int sampleCount, TimeSpan sampleDuration) =>
        durationSeconds is > 0 && (sampleDuration == TimeSpan.Zero || sampleDuration.TotalSeconds * sampleCount >= durationSeconds.Value * 0.85);
}
