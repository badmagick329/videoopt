namespace VideoOptimiser.Infrastructure.Immich;

/// <summary>An Immich job's original lives locally at <c>&lt;stagingDirectory&gt;/&lt;assetId&gt;/&lt;originalFileName&gt;</c>.</summary>
internal static class ImmichStaging
{
    /// <summary>
    /// True when <paramref name="path"/> sits directly in the asset's own folder under the staging directory. The job record
    /// comes from the database, so anything that writes or deletes a staged original checks this first: a path anywhere
    /// else is not a staged copy and may be a file the user cares about.
    /// </summary>
    public static bool IsStagedOriginal(string stagingDirectory, string? assetId, string path)
    {
        if (stagingDirectory.Length == 0 || !Guid.TryParseExact(assetId, "D", out _)) return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
        var folder = Path.Combine(root, assetId!);
        var full = Path.GetFullPath(path);
        return string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetDirectoryName(full), folder, StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileName(full).Length > 0;
    }
}
