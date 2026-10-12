namespace VideoOptimiser.Infrastructure.Configuration;

/// <summary>
/// Loads an API secret from an environment variable, then from a dotenv-style file. Secrets never live in the YAML, so a
/// shared or printed configuration cannot leak them.
/// </summary>
internal static class SecretLoader
{
    public static string? Load(string variable, string file)
    {
        if (!string.IsNullOrWhiteSpace(variable) && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value) return value;
        return !string.IsNullOrWhiteSpace(file) && File.Exists(file) && ReadDotEnvValue(file, variable) is { Length: > 0 } fileValue ? fileValue : null;
    }

    internal static string? ReadDotEnvValue(string path, string variable)
    {
        string? result = null;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var separator = line.IndexOf('=');
            if (separator < 1 || !string.Equals(line[..separator].Trim(), variable, StringComparison.Ordinal)) continue;
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))) value = value[1..^1];
            result = value;
        }
        return result;
    }
}
