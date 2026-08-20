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
            watch:
              roots:
                - path: "videos"
            """);

        var loaded = await new YamlConfigurationLoader().LoadAsync(configPath);

        loaded.Settings.Database.Path.Should().Be(Path.Combine(_directory, "state", "jobs.db"));
        loaded.Settings.Watch.Roots.Single().Path.Should().Be(Path.Combine(_directory, "videos"));
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
    public async Task LoadAsyncAppliesRemoteProcessingDefaultsAndResolvesSshToolPaths()
    {
        var configPath = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(configPath, """
            version: 1
            tools:
              sshPath: "tools/ssh.exe"
              sftpPath: "tools/sftp.exe"
            processing:
              mode: "remoteSsh"
              remoteSsh:
                host: "video-worker"
            """);

        var loaded = await new YamlConfigurationLoader().LoadAsync(configPath);

        loaded.Settings.Tools.SshPath.Should().Be(Path.Combine(_directory, "tools", "ssh.exe"));
        loaded.Settings.Tools.SftpPath.Should().Be(Path.Combine(_directory, "tools", "sftp.exe"));
        loaded.Settings.Processing.RemoteSsh.WorkingDirectory.Should().Be("/var/tmp/video-optimiser");
        loaded.Settings.Processing.RemoteSsh.MinimumCpuCount.Should().Be(8);
        loaded.Settings.Processing.RemoteSsh.MinimumAvailableMemory.Should().Be("14GiB");
        loaded.Settings.Processing.RemoteSsh.MinimumFreeDiskMultiplier.Should().Be(2.5);
    }

    [Fact]
    public async Task WriteAsyncCreatesEditableTemplateAndNeverOverwrites()
    {
        var destination = Path.Combine(_directory, "config.yaml");
        var writer = new YamlConfigurationTemplateWriter(new YamlConfigurationLoader());

        await writer.WriteAsync(destination);
        var contents = await File.ReadAllTextAsync(destination);
        contents.Should().Contain("roots:").And.Contain("# Folders searched by queue discover.").And.Contain("action: \"delete\"");
        contents.Should().Contain("mode: \"local\"").And.Contain("workingDirectory: \"/var/tmp/video-optimiser\"").And.Contain("sshPath: \"ssh\"");

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
