using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Cli;

/// <summary>
/// The one question <c>config init</c> asks: the quality level, which sets <c>quality.minimumVmaf</c>, the setting that
/// most decides whether a video is worth converting. The wording was approved by the user (VOPT-50); ask before changing it.
/// </summary>
internal static class ConfigInitQuestion
{
    /// <param name="flag">The <c>--quality</c> value, which skips the question.</param>
    /// <param name="interactive">False when input is redirected: a script or agent gets Standard and is told so.</param>
    public static QualityLevel Choose(QualityLevel? flag, bool interactive, TextReader input, TextWriter output)
    {
        if (flag is { } chosen) return chosen;
        if (!interactive)
        {
            output.WriteLine($"No console to ask on, so the quality target is {QualityLevels.Default} (VMAF {QualityLevels.MinimumVmaf(QualityLevels.Default)}). Pass --quality archive, standard or compact to choose.");
            return QualityLevels.Default;
        }

        output.WriteLine("Quality target?");
        for (var index = 0; index < QualityLevels.All.Count; index++)
        {
            var level = QualityLevels.All[index];
            var suffix = level == QualityLevels.Default ? " (default)" : string.Empty;
            output.WriteLine($"  {index + 1}. {level,-10}VMAF {QualityLevels.MinimumVmaf(level)}: {QualityLevels.Meaning(level)}{suffix}");
        }

        var defaultNumber = QualityLevels.All.ToList().IndexOf(QualityLevels.Default) + 1;
        while (true)
        {
            output.Write($"Choose 1-{QualityLevels.All.Count} [{defaultNumber}]: ");
            var answer = input.ReadLine();
            // End of input means nobody is left to answer, so it takes the default rather than asking forever.
            if (answer is null || answer.Trim().Length == 0) return QualityLevels.Default;
            if (int.TryParse(answer.Trim(), out var number) && number >= 1 && number <= QualityLevels.All.Count) return QualityLevels.All[number - 1];
            output.WriteLine("Enter 1, 2 or 3.");
        }
    }
}
