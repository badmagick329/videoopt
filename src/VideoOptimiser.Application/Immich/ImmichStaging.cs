using System.Security.Cryptography;

namespace VideoOptimiser.Application.Immich;

/// <summary>An Immich job's original lives locally at <c>&lt;stagingDirectory&gt;/&lt;assetId&gt;/&lt;originalFileName&gt;</c>.</summary>
public static class ImmichStaging
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

    // SHA-1 because that is Immich's checksum; it identifies the file, it does not protect anything.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "Matches Immich's SHA-1 asset checksum; not used for security.")]
    internal static async Task<string> Sha1Base64Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToBase64String(await SHA1.HashDataAsync(stream, cancellationToken));
    }

    /// <summary>Removes a staged copy and, if it is then empty, the asset's folder. Both are best effort.</summary>
    internal static void DeleteStaged(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Still open elsewhere; the next download of this asset overwrites it.
        }

        try
        {
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder still holds other files, or was never created.
        }
    }
}
