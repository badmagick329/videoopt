namespace VideoOptimiser.Application.Features.Config;

public interface IConfigurationTemplateWriter
{
    /// <summary>The full path <see cref="WriteAsync"/> writes to, so <c>config init</c> can refuse before it asks anything.</summary>
    string GetDestination(string? explicitPath);
    Task<string> WriteAsync(string? explicitPath, QualityLevel quality, CancellationToken cancellationToken = default);

    /// <summary>One refusal for both checks: <c>config init</c>'s before it asks, and the writer's when the file appears meanwhile.</summary>
    static InvalidOperationException RefuseOverwrite(string destination) =>
        new($"Refusing to overwrite the existing configuration file '{destination}'.");
}
