using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Configuration;

/// <param name="ConfiguredMode"><c>processing.mode</c> as the file says it, set only when <c>--local</c> or <c>--remote</c> replaced it in <see cref="Settings"/>.</param>
public sealed record LoadedConfiguration(AppSettings Settings, string Path, string? RawYaml = null, string? ConfiguredMode = null);

public interface IConfigurationLoader
{
    Task<LoadedConfiguration> LoadAsync(string? explicitPath, CancellationToken cancellationToken = default);
    string GetDefaultConfigurationPath();
}

/// <summary>
/// Reads the process's environment variables. A port so a test supplies its own values: the real environment is shared
/// by every test running in parallel, so one test setting a fixed-name variable would change what another loads.
/// </summary>
public interface IEnvironmentVariables
{
    string? Read(string name);
}

public interface ISettingsValidator
{
    IReadOnlyList<Diagnostic> Validate(AppSettings settings);
}
