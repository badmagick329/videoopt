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
    /// <summary>The full path <see cref="WriteAsync"/> writes to, so <c>config init</c> can refuse before it asks anything.</summary>
    string GetDestination(string? explicitPath);
    Task<string> WriteAsync(string? explicitPath, QualityLevel quality, CancellationToken cancellationToken = default);
}
