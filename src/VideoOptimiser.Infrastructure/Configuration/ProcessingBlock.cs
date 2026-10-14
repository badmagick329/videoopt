using System.Globalization;
using System.Text;
using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Infrastructure.Configuration;

/// <summary>
/// Renders and splices the top-level <c>processing:</c> section as text. The section is edited as text, not by
/// serialising the whole file, so every comment and key outside it survives untouched.
/// </summary>
internal static class ProcessingBlock
{
    /// <summary>Every field, in the template's order, so a rerun shows the user the complete section it would write.</summary>
    public static string Render(ProcessingSettings processing)
    {
        var remote = processing.RemoteSsh;
        var hetzner = remote.Hetzner;
        var lines = new List<string>
        {
            "processing:",
            "  # Run CRF search and encoding locally or over SSH.",
            $"  mode: {Quote(processing.Mode)}",
            "  remoteSsh:",
            "    # manual uses this OpenSSH host/config alias. hetzner fills it at runtime.",
            $"    lifecycle: {Quote(remote.Lifecycle)}",
            $"    host: {Quote(remote.Host)}",
            "    # Leave these empty when the manual SSH alias already supplies them.",
            $"    user: {Quote(remote.User)}",
            $"    identityFile: {Quote(remote.IdentityFile)}",
            $"    knownHostsFile: {Quote(remote.KnownHostsFile)}",
            $"    workingDirectory: {Quote(remote.WorkingDirectory)}",
            $"    minimumCpuCount: {remote.MinimumCpuCount.ToString(CultureInfo.InvariantCulture)}",
            $"    minimumAvailableMemory: {Quote(remote.MinimumAvailableMemory)}",
            "    # Remote free space must be at least this multiple of each source file.",
            $"    minimumFreeDiskMultiplier: {remote.MinimumFreeDiskMultiplier.ToString(CultureInfo.InvariantCulture)}",
            "    hetzner:",
            "      # Used only when lifecycle is hetzner. An existing environment variable takes precedence.",
            $"      apiTokenEnvironmentVariable: {Quote(hetzner.ApiTokenEnvironmentVariable)}",
            "      # Loaded automatically when the environment variable is not set. Keep this file ignored.",
            $"      apiTokenFile: {Quote(hetzner.ApiTokenFile)}",
            "      # Tried in this order, each in every location below. Each needs x86, minimumCpuCount cores and minimumAvailableMemory.",
            $"      serverTypes: {List(hetzner.ServerTypes)}",
            "      # Skips any type and location costing more than this, in EUR per hour including VAT. null removes the cap.",
            $"      maxHourlyPrice: {(hetzner.MaxHourlyPrice is { } price ? price.ToString(CultureInfo.InvariantCulture) : "null")}",
            $"      image: {Quote(hetzner.Image)}",
            "      # Candidates are tried in this order after the availability preflight.",
            $"      locations: {List(hetzner.Locations)}",
            "      # The name of the SSH key in your Hetzner project; remote setup uploads it.",
            $"      sshKeyName: {Quote(hetzner.SshKeyName)}",
            $"      serverNamePrefix: {Quote(hetzner.ServerNamePrefix)}",
            $"      bootstrapScriptPath: {Quote(hetzner.BootstrapScriptPath)}",
            $"      bootstrapTimeout: {Quote(hetzner.BootstrapTimeout)}",
            "      # Delete after a clean run. Interrupted or unreachable work is retained.",
            $"      deleteAfterRun: {(hetzner.DeleteAfterRun ? "true" : "false")}"
        };
        return string.Join('\n', lines) + '\n';
    }

    /// <summary>
    /// Swaps the column-0 <c>processing:</c> section for <paramref name="block"/>, or appends it when the file has none. The
    /// section ends at the next column-0 key; blank lines and column-0 comments just before that key belong to it, so they
    /// stay where they are. The file's line ending style is kept.
    /// </summary>
    public static string Replace(string yaml, string block)
    {
        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var replacement = block.ReplaceLineEndings("\n").TrimEnd('\n').Replace("\n", newline, StringComparison.Ordinal) + newline;
        var lines = yaml.Split('\n');
        var start = Array.FindIndex(lines, IsProcessingKey);
        if (start < 0)
        {
            var terminated = yaml.Length == 0 || yaml.EndsWith('\n') ? yaml : yaml + newline;
            var endsWithBlankLine = terminated.EndsWith("\n\n", StringComparison.Ordinal) || terminated.EndsWith("\n\r\n", StringComparison.Ordinal);
            return terminated + (terminated.Length == 0 || endsWithBlankLine ? string.Empty : newline) + replacement;
        }

        var end = start + 1;
        while (end < lines.Length && !StartsNextKey(lines[end])) end++;
        while (end > start + 1 && (string.IsNullOrWhiteSpace(lines[end - 1]) || lines[end - 1][0] == '#')) end--;

        var result = new StringBuilder();
        for (var index = 0; index < start; index++) result.Append(lines[index]).Append('\n');
        result.Append(replacement);
        for (var index = end; index < lines.Length; index++)
        {
            result.Append(lines[index]);
            if (index < lines.Length - 1) result.Append('\n');
        }
        return result.ToString();
    }

    private static bool IsProcessingKey(string line) =>
        line.StartsWith("processing:", StringComparison.Ordinal) && (line.Length == "processing:".Length || char.IsWhiteSpace(line["processing:".Length]));

    private static bool StartsNextKey(string line) => line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#';

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Quote)) + "]";

    // Double-quoted so a Windows path's backslashes and a stray quote cannot change what the value means.
    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': text.Append("\\\\"); break;
                case '"': text.Append("\\\""); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case < ' ': text.Append("\\x").Append(((int)character).ToString("x2", CultureInfo.InvariantCulture)); break;
                default: text.Append(character); break;
            }
        }
        return text.Append('"').ToString();
    }
}
