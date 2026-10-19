namespace VideoOptimiser.Infrastructure.Tools;

/// <summary>
/// Quoting for the POSIX shell at the far end of an ssh connection. Shared, not in the Run slice, because doctor's probe
/// quotes its script the same way and slices never reach into each other's Infrastructure.
/// </summary>
internal static class PosixShell
{
    /// <summary>
    /// Single-quotes <paramref name="value"/> so the shell reads it back as one word with nothing expanded: each quote inside
    /// closes the quoting, adds a double-quoted quote and reopens it.
    /// </summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
