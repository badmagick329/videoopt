using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Infrastructure.Configuration;

namespace VideoOptimiser.UnitTests;

public sealed class ConfigurationInfrastructureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public ConfigurationInfrastructureTests() => Directory.CreateDirectory(_directory);

    /// <summary>One load pass applies the defaults of every section and resolves each relative path against the config's folder.</summary>
    [Fact]
    public async Task LoadAsyncAppliesDefaultsAndResolvesRelativeConfiguredPathsAgainstConfigDirectory()
    {
        var settings = (await LoadAsync("""
            version: 1
            database:
              path: "state/jobs.db"
            watch:
              roots:
                - path: "videos"
            tools:
              sshPath: "tools/ssh.exe"
              sftpPath: "tools/sftp.exe"
            immich:
              baseUrl: "http://192.168.1.113:2283"
            processing:
              mode: "remoteSsh"
              remoteSsh:
                host: "video-worker"
            """)).Settings;

        settings.Database.Path.Should().Be(Path.Combine(_directory, "state", "jobs.db"));
        settings.Watch.Roots.Single().Path.Should().Be(Path.Combine(_directory, "videos"));
        settings.Tools.SshPath.Should().Be(Path.Combine(_directory, "tools", "ssh.exe"));
        settings.Tools.SftpPath.Should().Be(Path.Combine(_directory, "tools", "sftp.exe"));
        var immich = settings.Immich;
        immich.ApiKeyEnvironmentVariable.Should().Be("VIDEO_OPTIMISER_IMMICH_API_KEY");
        immich.ApiKeyFile.Should().Be(Path.Combine(_directory, ".env"));
        immich.StagingDirectory.Should().Be(Path.Combine(_directory, "immich-staging"));
        immich.MinimumAgeDays.Should().Be(90);
        immich.SkipFavorites.Should().BeTrue();
        var remote = settings.Processing.RemoteSsh;
        remote.WorkingDirectory.Should().Be("/var/tmp/video-optimiser");
        remote.MinimumCpuCount.Should().Be(8);
        remote.MinimumAvailableMemory.Should().Be("14GiB");
        remote.MinimumFreeDiskMultiplier.Should().Be(2.5);
        remote.Lifecycle.Should().Be(RemoteLifecycleModes.Manual);
        remote.Hetzner.ServerTypes.Should().Equal("cx43", "cx53", "cpx42", "ccx33");
        remote.Hetzner.MaxHourlyPrice.Should().Be(0.30m);
        remote.Hetzner.ApiTokenFile.Should().Be(Path.Combine(_directory, ".env"));
        remote.Hetzner.Locations.Should().Equal("hel1", "fsn1", "nbg1");
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

    /// <summary>
    /// The loader's one message for a key it does not know, which the removed <c>quality.crfSearch.enabled</c> switch also
    /// gets: the full path, the position and the hint.
    /// </summary>
    [Fact]
    public async Task LoadAsyncRejectsUnknownKeyAndNamesItsFullPath()
    {
        var load = () => LoadAsync("""
            version: 1
            processing:
              remoteSsh:
                hetzner:
                  serverTypes: ["cx43"]
                  ocations: ["hel1"]
            """);

        (await load.Should().ThrowAsync<InvalidDataException>())
            .WithMessage("Unknown setting 'processing.remoteSsh.hetzner.ocations' at line 6, column 7. Check its spelling, or remove it if it is no longer supported.");
    }

    [Fact]
    public async Task LoadAsyncRejectsTheSingleServerTypeAndNamesItsReplacement()
    {
        var load = () => LoadAsync("""
            version: 1
            processing:
              remoteSsh:
                hetzner:
                  serverType: "cx43"
            """);

        (await load.Should().ThrowAsync<InvalidDataException>())
            .WithMessage("Unknown setting 'processing.remoteSsh.hetzner.serverType' at line 5, column 7. It was replaced by serverTypes*");
    }

    [Fact]
    public async Task LoadAsyncKeepsAnEmptyServerTypeListForValidationToReject()
    {
        var loaded = await LoadAsync("""
            version: 1
            processing:
              remoteSsh:
                hetzner:
                  serverTypes:
            """);

        loaded.Settings.Processing.RemoteSsh.Hetzner.ServerTypes.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsyncNamesTheSettingWhoseValueHasTheWrongType()
    {
        var load = () => LoadAsync("""
            version: 1
            quality:
              crfSearch:
                minCrf: "low"
            """);

        (await load.Should().ThrowAsync<InvalidDataException>()).WithMessage("Invalid value for 'quality.crfSearch.minCrf' at line 4, column 13:*");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<LoadedConfiguration> LoadAsync(string yaml)
    {
        var configPath = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(configPath, yaml);
        return await new YamlConfigurationLoader().LoadAsync(configPath);
    }
}
