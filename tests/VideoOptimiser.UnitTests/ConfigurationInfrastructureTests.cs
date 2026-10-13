using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Infrastructure.Configuration;

namespace VideoOptimiser.UnitTests;

public sealed class ConfigurationInfrastructureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public ConfigurationInfrastructureTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task LoadAsyncAppliesImmichDefaultsAndResolvesItsPathsAgainstConfigDirectory()
    {
        var configPath = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(configPath, """
            version: 1
            immich:
              baseUrl: "http://192.168.1.113:2283"
            """);

        var immich = (await new YamlConfigurationLoader().LoadAsync(configPath)).Settings.Immich;

        immich.ApiKeyEnvironmentVariable.Should().Be("VIDEO_OPTIMISER_IMMICH_API_KEY");
        immich.ApiKeyFile.Should().Be(Path.Combine(_directory, ".env"));
        immich.StagingDirectory.Should().Be(Path.Combine(_directory, "immich-staging"));
        immich.MinimumAgeDays.Should().Be(90);
        immich.SkipFavorites.Should().BeTrue();
    }

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
        loaded.Settings.Processing.RemoteSsh.Lifecycle.Should().Be(RemoteLifecycleModes.Manual);
        loaded.Settings.Processing.RemoteSsh.Hetzner.ServerTypes.Should().Equal("cx43", "cx53", "cpx42", "ccx33");
        loaded.Settings.Processing.RemoteSsh.Hetzner.MaxHourlyPrice.Should().Be(0.30m);
        loaded.Settings.Processing.RemoteSsh.Hetzner.ApiTokenFile.Should().Be(Path.Combine(_directory, ".env"));
        loaded.Settings.Processing.RemoteSsh.Hetzner.Locations.Should().Equal("hel1", "fsn1", "nbg1");
    }

    [Fact]
    public async Task LoadAsyncRejectsUnknownKeyAndNamesItsFullPath()
    {
        var configPath = Path.Combine(_directory, "typo.yaml");
        await File.WriteAllTextAsync(configPath, """
            version: 1
            processing:
              remoteSsh:
                hetzner:
                  serverTypes: ["cx43"]
                  ocations: ["hel1"]
            """);

        var load = () => new YamlConfigurationLoader().LoadAsync(configPath);

        (await load.Should().ThrowAsync<InvalidDataException>())
            .WithMessage("Unknown setting 'processing.remoteSsh.hetzner.ocations' at line 6, column 7.*");
    }

    [Fact]
    public async Task LoadAsyncRejectsTheSingleServerTypeAndNamesItsReplacement()
    {
        var configPath = Path.Combine(_directory, "server-type.yaml");
        await File.WriteAllTextAsync(configPath, """
            version: 1
            processing:
              remoteSsh:
                hetzner:
                  serverType: "cx43"
            """);

        var load = () => new YamlConfigurationLoader().LoadAsync(configPath);

        (await load.Should().ThrowAsync<InvalidDataException>())
            .WithMessage("Unknown setting 'processing.remoteSsh.hetzner.serverType' at line 5, column 7. It was replaced by serverTypes*");
    }

    [Fact]
    public async Task LoadAsyncKeepsAnEmptyServerTypeListForValidationToReject()
    {
        var configPath = Path.Combine(_directory, "no-server-types.yaml");
        await File.WriteAllTextAsync(configPath, """
            version: 1
            processing:
              remoteSsh:
                hetzner:
                  serverTypes:
            """);

        var loaded = await new YamlConfigurationLoader().LoadAsync(configPath);

        loaded.Settings.Processing.RemoteSsh.Hetzner.ServerTypes.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsyncNamesTheSettingWhoseValueHasTheWrongType()
    {
        var configPath = Path.Combine(_directory, "type.yaml");
        await File.WriteAllTextAsync(configPath, """
            version: 1
            quality:
              crfSearch:
                minCrf: "low"
            """);

        var load = () => new YamlConfigurationLoader().LoadAsync(configPath);

        (await load.Should().ThrowAsync<InvalidDataException>()).WithMessage("Invalid value for 'quality.crfSearch.minCrf' at line 4, column 13:*");
    }

    [Fact]
    public async Task LoadAsyncRejectsTheRemovedCrfSearchSwitch()
    {
        // Nothing ever read it: CRF search always ran, so false silently did nothing.
        var configPath = Path.Combine(_directory, "crf-search-enabled.yaml");
        await File.WriteAllTextAsync(configPath, """
            version: 1
            quality:
              crfSearch:
                enabled: false
            """);

        var load = () => new YamlConfigurationLoader().LoadAsync(configPath);

        (await load.Should().ThrowAsync<InvalidDataException>())
            .WithMessage("Unknown setting 'quality.crfSearch.enabled' at line 4, column 5. Check its spelling, or remove it if it is no longer supported.");
    }

    [Fact]
    public async Task LoadAsyncAcceptsTheConfigInitTemplate()
    {
        var configPath = Path.Combine(_directory, "template.yaml");
        await new YamlConfigurationTemplateWriter(new YamlConfigurationLoader()).WriteAsync(configPath, QualityLevel.Standard);

        var loaded = await new YamlConfigurationLoader().LoadAsync(configPath);

        loaded.Settings.Processing.RemoteSsh.Hetzner.Locations.Should().Equal("hel1", "fsn1", "nbg1");
        loaded.Settings.Processing.RemoteSsh.Hetzner.ServerTypes.Should().Equal("cx43", "cx53", "cpx42", "ccx33");
        loaded.Settings.Processing.RemoteSsh.Hetzner.MaxHourlyPrice.Should().Be(0.30m);
        loaded.Settings.Quality.MaxEncodedPercent.Should().Be(80);
    }

    [Fact]
    public async Task WriteAsyncCreatesEditableTemplateAndNeverOverwrites()
    {
        var destination = Path.Combine(_directory, "config.yaml");
        var writer = new YamlConfigurationTemplateWriter(new YamlConfigurationLoader());

        await writer.WriteAsync(destination, QualityLevel.Standard);
        var contents = await File.ReadAllTextAsync(destination);
        contents.Should().Contain("roots:").And.Contain("# Folders searched by queue discover.").And.Contain("action: \"delete\"").And.Contain("locations: [\"hel1\", \"fsn1\", \"nbg1\"]");
        contents.Should().Contain("mode: \"local\"").And.Contain("lifecycle: \"manual\"").And.Contain("serverTypes: [\"cx43\", \"cx53\", \"cpx42\", \"ccx33\"]").And.Contain("maxEncodedPercent: 80").And.Contain("maxHourlyPrice: 0.30").And.Contain("workingDirectory: \"/var/tmp/video-optimiser\"").And.Contain("sshPath: \"ssh\"");

        var action = () => writer.WriteAsync(destination, QualityLevel.Standard);
        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(QualityLevel.Archive, 97, "  # Archive: indistinguishable from the original. Standard is 95, Compact is 93.")]
    [InlineData(QualityLevel.Standard, 95, "  # Standard: hard to tell apart from the original. Archive is 97, Compact is 93.")]
    [InlineData(QualityLevel.Compact, 93, "  # Compact: small visible loss, bigger savings. Archive is 97, Standard is 95.")]
    public async Task EachQualityLevelWritesItsVmafAndComment(QualityLevel level, double vmaf, string comment)
    {
        var destination = Path.Combine(_directory, $"{level}.yaml");
        await new YamlConfigurationTemplateWriter(new YamlConfigurationLoader()).WriteAsync(destination, level);

        var lines = await File.ReadAllLinesAsync(destination);
        var loaded = await new YamlConfigurationLoader().LoadAsync(destination);

        loaded.Settings.Quality.MinimumVmaf.Should().Be(vmaf);
        lines.Should().ContainInConsecutiveOrder("quality:", comment, "  # Minimum visual quality target used by CRF search.", $"  minimumVmaf: {vmaf}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
