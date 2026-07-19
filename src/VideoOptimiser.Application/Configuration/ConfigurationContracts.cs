using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Configuration;

public sealed record LoadedConfiguration(AppSettings Settings, string Path, string? RawYaml = null);

public interface IConfigurationLoader
{
    Task<LoadedConfiguration> LoadAsync(string? explicitPath, CancellationToken cancellationToken = default);
    string GetDefaultConfigurationPath();
}

public interface ISettingsValidator
{
    IReadOnlyList<Diagnostic> Validate(AppSettings settings);
}

public interface IConfigurationTemplateWriter
{
    Task<string> WriteAsync(string? explicitPath, CancellationToken cancellationToken = default);
}
