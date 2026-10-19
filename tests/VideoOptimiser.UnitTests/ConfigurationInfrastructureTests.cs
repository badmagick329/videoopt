using System.Reflection;
using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Infrastructure.Configuration;
using YamlDotNet.Serialization;

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
        remote.StageSilenceTimeout.Should().Be("30m");
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

    /// <summary>An absent count must stay null, not a default, or every config would opt out of the duration rule (VOPT-159).</summary>
    [Theory]
    [InlineData("", null)]
    [InlineData("sampleCount: 7", 7)]
    public async Task LoadAsyncKeepsAnAbsentSampleCountUnset(string line, int? expected)
    {
        var loaded = await LoadAsync($"""
            version: 1
            quality:
              crfSearch:
                {line}
            """);

        loaded.Settings.Quality.CrfSearch.ConfiguredSampleCount.Should().Be(expected);
    }

    /// <summary>
    /// The null shapes the per-setting cases below don't reach: nothing after the colon inside a list item, a number, which
    /// fails while deserialising, and several at once, each named (VOPT-164).
    /// </summary>
    [Theory]
    [InlineData("eligibility:\n  rules:\n    - codecs: [\"h264\"]\n      resolution:", "eligibility.rules[0].resolution can't be empty.")]
    [InlineData("processing:\n  remoteSsh:\n    minimumCpuCount: null", "processing.remoteSsh.minimumCpuCount can't be empty.")]
    [InlineData("database:\n  path: ~\nquality:\n  encoder: ~", "database.path can't be empty. quality.encoder can't be empty.")]
    public async Task LoadAsyncNamesEachSettingLeftEmpty(string yaml, string expected)
    {
        var load = () => LoadAsync($"version: 1\n{yaml}");

        (await load.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Be(expected);
    }

    /// <summary>The settings where a null is not an error: an optional text setting is unset, and a list is empty or, for locations, the default.</summary>
    [Fact]
    public async Task LoadAsyncTakesANullOptionalSettingAsUnsetAndANullListAsEmpty()
    {
        var settings = (await LoadAsync("""
            version: 1
            processing:
              remoteSsh:
                host: ~
                hetzner:
                  locations: ~
            eligibility:
              rules:
                - codecs: ~
            """)).Settings;

        settings.Processing.RemoteSsh.Host.Should().BeEmpty();
        settings.Processing.RemoteSsh.Hetzner.Locations.Should().Equal("hel1", "fsn1", "nbg1");
        settings.Eligibility.Rules.Single().Codecs.Should().BeEmpty();
    }

    /// <summary>
    /// Every setting in the model, and every list's item, written as null, either fails to load with a message naming it or
    /// loads with nothing null left in the settings. A setting added later without a decision for null fails here (VOPT-164).
    /// </summary>
    [Fact]
    public void ParseUnresolvedLetsNoNullThrough()
    {
        var failures = new List<string>();
        foreach (var (path, yaml) in NullCases(typeof(AppSettings), "", inner => $"{{{inner}}}"))
        {
            try
            {
                var settings = YamlConfigurationLoader.ParseUnresolved(yaml);
                failures.AddRange(NullsIn(settings, "").Select(found => $"{yaml} loaded with {found} null"));
            }
            catch (InvalidDataException exception)
            {
                if (exception.Message != $"{path} can't be empty.") failures.Add($"{yaml} failed with: {exception.Message}");
            }
        }

        failures.Should().BeEmpty();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>One flow-style YAML document per setting and list item, with only that one null; <paramref name="wrap"/> nests it in its parents.</summary>
    private static IEnumerable<(string Path, string Yaml)> NullCases(Type type, string parent, Func<string, string> wrap)
    {
        foreach (var property in type.GetProperties())
        {
            if (property.GetCustomAttribute<YamlMemberAttribute>()?.Alias is not { } alias) continue;
            var path = parent.Length == 0 ? alias : $"{parent}.{alias}";
            yield return (path, wrap($"{alias}: null"));

            var propertyType = property.PropertyType;
            if (IsSection(propertyType))
            {
                foreach (var nested in NullCases(propertyType, path, inner => wrap($"{alias}: {{{inner}}}"))) yield return nested;
            }
            else if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(List<>))
            {
                yield return ($"{path}[0]", wrap($"{alias}: [null]"));
                var item = propertyType.GetGenericArguments()[0];
                if (!IsSection(item)) continue;
                foreach (var nested in NullCases(item, $"{path}[0]", inner => wrap($"{alias}: [{{{inner}}}]"))) yield return nested;
            }
        }
    }

    private static IEnumerable<string> NullsIn(object settings, string parent)
    {
        foreach (var property in settings.GetType().GetProperties())
        {
            if (property.GetCustomAttribute<YamlMemberAttribute>()?.Alias is not { } alias || property.PropertyType.IsValueType) continue;
            var path = parent.Length == 0 ? alias : $"{parent}.{alias}";
            switch (property.GetValue(settings))
            {
                case null:
                    yield return path;
                    break;
                case System.Collections.IList list:
                    for (var index = 0; index < list.Count; index++)
                    {
                        if (list[index] is not { } item) yield return $"{path}[{index}]";
                        else if (IsSection(item.GetType())) foreach (var nested in NullsIn(item, $"{path}[{index}]")) yield return nested;
                    }
                    break;
                case var section when IsSection(section.GetType()):
                    foreach (var nested in NullsIn(section, path)) yield return nested;
                    break;
            }
        }
    }

    private static bool IsSection(Type type) => type.GetCustomAttribute<YamlSerializableAttribute>() is not null;

    private async Task<LoadedConfiguration> LoadAsync(string yaml)
    {
        var configPath = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(configPath, yaml);
        return await new YamlConfigurationLoader().LoadAsync(configPath);
    }
}
