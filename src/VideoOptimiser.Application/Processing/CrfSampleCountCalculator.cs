namespace VideoOptimiser.Application.Processing;

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
}
