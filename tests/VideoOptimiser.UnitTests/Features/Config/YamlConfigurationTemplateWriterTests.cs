using FluentAssertions;
using VideoOptimiser.Application.Features.Config;
using VideoOptimiser.Infrastructure.Configuration;
using VideoOptimiser.Infrastructure.Features.Config;

namespace VideoOptimiser.UnitTests.Features.Config;

public sealed class YamlConfigurationTemplateWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public YamlConfigurationTemplateWriterTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task WriteAsyncCreatesEditableTemplateAndNeverOverwrites()
    {
        var destination = Path.Combine(_directory, "config.yaml");
        var writer = new YamlConfigurationTemplateWriter(new YamlConfigurationLoader());

        await writer.WriteAsync(destination, QualityLevel.Standard);
        var contents = await File.ReadAllTextAsync(destination);
        contents.Should().Contain("roots:").And.Contain("# Folders searched by queue add.").And.Contain("action: \"delete\"").And.Contain("locations: [\"hel1\", \"fsn1\", \"nbg1\"]");
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
        loaded.Settings.Processing.RemoteSsh.Hetzner.Locations.Should().Equal("hel1", "fsn1", "nbg1");
        loaded.Settings.Processing.RemoteSsh.Hetzner.ServerTypes.Should().Equal("cx43", "cx53", "cpx42", "ccx33");
        loaded.Settings.Processing.RemoteSsh.Hetzner.MaxHourlyPrice.Should().Be(0.30m);
        loaded.Settings.Quality.MaxEncodedPercent.Should().Be(80);
        lines.Should().ContainInConsecutiveOrder("quality:", comment, "  # Minimum visual quality target used by CRF search.", $"  minimumVmaf: {vmaf}");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
