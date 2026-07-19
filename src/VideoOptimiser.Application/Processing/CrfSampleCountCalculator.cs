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
        if (minutes <= 2)
        {
            return 2;
        }

        if (minutes <= 4)
        {
            return (int)Math.Ceiling(minutes);
        }

        return Math.Min(12, 4 + (int)Math.Ceiling((minutes - 4) / 3));
    }
}
