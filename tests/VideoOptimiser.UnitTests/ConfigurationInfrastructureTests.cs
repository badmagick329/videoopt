using FluentAssertions;
using VideoOptimiser.Infrastructure.Configuration;

namespace VideoOptimiser.UnitTests;

public sealed class ConfigurationInfrastructureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public ConfigurationInfrastructureTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task LoadAsyncResolvesRelativeConfiguredPathsAgainstConfigDirectory()
    {
        var configPath = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(configPath, """
            version: 1
            database:
              path: "state/jobs.db"
            logging:
              directory: "state/logs"
            watch:
              roots:
                - path: "videos"
            original:
              archiveDirectory: "archive"
            """);

        var loaded = await new YamlConfigurationLoader().LoadAsync(configPath);

        loaded.Settings.Database.Path.Should().Be(Path.Combine(_directory, "state", "jobs.db"));
        loaded.Settings.Logging.Directory.Should().Be(Path.Combine(_directory, "state", "logs"));
        loaded.Settings.Watch.Roots.Single().Path.Should().Be(Path.Combine(_directory, "videos"));
        loaded.Settings.Original.ArchiveDirectory.Should().Be(Path.Combine(_directory, "archive"));
    }

    [Fact]
    public async Task LoadAsyncUsesEnvironmentConfigurationPathWhenNoExplicitPathIsProvided()
    {
        var configPath = Path.Combine(_directory, "environment.yaml");
        await File.WriteAllTextAsync(configPath, "version: 1");
        var original = Environment.GetEnvironmentVariable("VIDEO_OPTIMISER_CONFIG");
        Environment.SetEnvironmentVariable("VIDEO_OPTIMISER_CONFIG", configPath);

        try
        {
            var loaded = await new YamlConfigurationLoader().LoadAsync(null);
            loaded.Path.Should().Be(configPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VIDEO_OPTIMISER_CONFIG", original);
        }
    }

    [Fact]
    public async Task WriteAsyncCreatesEditableTemplateAndNeverOverwrites()
    {
        var destination = Path.Combine(_directory, "config.yaml");
        var writer = new YamlConfigurationTemplateWriter(new YamlConfigurationLoader());

        await writer.WriteAsync(destination);
        var contents = await File.ReadAllTextAsync(destination);
        contents.Should().Contain("roots: []").And.Contain("archiveDirectory: \"\"");

        var action = () => writer.WriteAsync(destination);
        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
