namespace VideoOptimiser.Application.Features.Config;

/// <summary>
/// The quality question <c>config init</c> asks. It exists only there and in the comment it writes: the config keeps
/// <c>quality.minimumVmaf</c> alone, so a value the user edits later wins and there is no second key to fall out of step.
/// </summary>
public enum QualityLevel
{
    Archive,
    Standard,
    Compact
}

public static class QualityLevels
{
    public const QualityLevel Default = QualityLevel.Standard;

    /// <summary>The levels in the order <c>config init</c> numbers them.</summary>
    public static IReadOnlyList<QualityLevel> All { get; } = [QualityLevel.Archive, QualityLevel.Standard, QualityLevel.Compact];

    public static int MinimumVmaf(QualityLevel level) => level switch
    {
        QualityLevel.Archive => 97,
        QualityLevel.Standard => 95,
        QualityLevel.Compact => 93,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
    };

    public static string Meaning(QualityLevel level) => level switch
    {
        QualityLevel.Archive => "indistinguishable from the original",
        QualityLevel.Standard => "hard to tell apart",
        QualityLevel.Compact => "small visible loss, bigger savings",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
    };

    /// <summary>Reads the <c>--quality</c> value: the level's name in any case.</summary>
    public static bool TryParse(string? value, out QualityLevel level)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                level = candidate;
                return true;
            }
        }

        level = Default;
        return false;
    }

    /// <summary>The comment written above <c>minimumVmaf</c>, naming the chosen level and the other two.</summary>
    public static string TemplateComment(QualityLevel level)
    {
        var meaning = level == QualityLevel.Standard ? "hard to tell apart from the original" : Meaning(level);
        var others = All.Where(other => other != level).Select(other => $"{other} is {MinimumVmaf(other)}");
        return $"{level}: {meaning}. {string.Join(", ", others)}.";
    }
}
