using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.TemporaryOutputs;

namespace VideoOptimiser.Application.Eligibility;

/// <summary>
/// The files video-optimiser made: temporary outputs and stage logs in any <c>.video-optimiser</c> folder, and Immich
/// downloaded copies under <c>immich.stagingDirectory</c>. A folder scan never descends into these folders, and
/// <c>queue add &lt;file&gt;</c> refuses a file in one even with <c>--force</c> (R10): converting it would replace the
/// tool's own file, one nothing reads, instead of the user's video. These exclusions are deliberately not configurable,
/// unlike <c>eligibility.excludedDirectories</c> and <c>excludedNamePatterns</c>, which a user may empty or replace.
/// </summary>
public static class ToolFiles
{
    public static bool IsMadeByTool(string path, AppSettings settings)
    {
        var staging = StagingFolder(settings);
        for (var directory = Path.GetDirectoryName(Path.GetFullPath(path)); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            if (IsToolFolder(directory, staging)) return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="directory"/>, a full path, is one of the tool's folders, which a scan does not descend into.</summary>
    public static bool IsToolFolder(string directory, AppSettings settings) => IsToolFolder(directory, StagingFolder(settings));

    private static bool IsToolFolder(string directory, string? staging) =>
        string.Equals(Path.GetFileName(directory), TemporaryOutputClassifier.FolderName, StringComparison.OrdinalIgnoreCase) ||
        (staging is not null && string.Equals(Path.TrimEndingDirectorySeparator(directory), staging, StringComparison.OrdinalIgnoreCase));

    private static string? StagingFolder(AppSettings settings) =>
        settings.Immich.StagingDirectory.Length > 0 ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.Immich.StagingDirectory)) : null;
}
