using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Configuration;

/// <param name="ConfiguredMode"><c>processing.mode</c> as the file says it, set only when <c>--local</c> or <c>--remote</c> replaced it in <see cref="Settings"/>.</param>
public sealed record LoadedConfiguration(AppSettings Settings, string Path, string? RawYaml = null, string? ConfiguredMode = null);

public interface IConfigurationLoader
{
    Task<LoadedConfiguration> LoadAsync(string? explicitPath, CancellationToken cancellationToken = default);
    string GetDefaultConfigurationPath();
}

public interface ISettingsValidator
{
    IReadOnlyList<Diagnostic> Validate(AppSettings settings);
}
