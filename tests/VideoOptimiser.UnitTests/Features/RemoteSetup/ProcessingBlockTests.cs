using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Infrastructure.Configuration;
using VideoOptimiser.Infrastructure.Features.RemoteSetup;

namespace VideoOptimiser.UnitTests.Features.RemoteSetup;

public sealed class ProcessingBlockTests
{
    [Fact]
    public void RenderedBlockRoundTripsThroughTheLoaderToTheSameValues()
    {
        var settings = new ProcessingSettings
        {
            Mode = ProcessingModes.RemoteSsh,
            RemoteSsh = new RemoteSshSettings
            {
                Host = "worker \"one\"",
                User = "me",
                IdentityFile = @"C:\Users\me\.ssh\id ""odd"" key",
                KnownHostsFile = "known\thosts",
                Lifecycle = RemoteLifecycleModes.Hetzner,
                WorkingDirectory = "/srv/work",
                MinimumCpuCount = 4,
                MinimumAvailableMemory = "7GiB",
                MinimumFreeDiskMultiplier = 3.25,
                Hetzner = new HetznerSettings
                {
                    ApiTokenEnvironmentVariable = "MY_TOKEN",
                    ApiTokenFile = @"secrets\.env",
                    ServerTypes = ["cx43", "ccx33"],
                    MaxHourlyPrice = 0.125m,
                    Image = "debian-12",
                    Locations = ["fsn1"],
                    SshKeyName = "my-key",
                    ServerNamePrefix = "vopt",
                    BootstrapScriptPath = @"..\scripts\bootstrap.sh",
                    BootstrapTimeout = "30m",
                    DeleteAfterRun = false
                }
            }
        };

        var parsed = Parse(settings);

        parsed.Should().BeEquivalentTo(settings);
    }

    [Fact]
    public void RenderedDefaultsRoundTripAndAnUncappedPriceStaysNull()
    {
        var defaults = new ProcessingSettings();
        var uncapped = new ProcessingSettings { RemoteSsh = { Hetzner = { MaxHourlyPrice = null, ServerTypes = [] } } };

        Parse(defaults).Should().BeEquivalentTo(defaults);
        ProcessingBlock.Render(defaults).Should().Contain("maxHourlyPrice: 0.30").And.Contain("serverTypes: [\"cx43\", \"cx53\", \"cpx42\", \"ccx33\"]");
        ProcessingBlock.Render(uncapped).Should().Contain("maxHourlyPrice: null").And.Contain("serverTypes: []");
        Parse(uncapped).Should().BeEquivalentTo(uncapped);
    }

    [Fact]
    public void ReplaceKeepsEverythingOutsideTheSectionAndTheCommentsBeforeTheNextKey()
    {
        const string yaml = "# top\nversion: 1\n\nprocessing:\n  mode: \"local\"\n  remoteSsh:\n    host: \"\"\n\n# the database\n# holds jobs\ndatabase:\n  path: \"jobs.db\"\n";

        var result = ProcessingBlock.Replace(yaml, "processing:\n  mode: \"remoteSsh\"\n");

        result.Should().Be("# top\nversion: 1\n\nprocessing:\n  mode: \"remoteSsh\"\n\n# the database\n# holds jobs\ndatabase:\n  path: \"jobs.db\"\n");
    }

    [Fact]
    public void ReplaceKeepsCrlfLineEndingsThroughout()
    {
        const string yaml = "version: 1\r\nprocessing:\r\n  mode: \"local\"\r\n\r\n# next\r\ndatabase:\r\n  path: \"jobs.db\"\r\n";

        var result = ProcessingBlock.Replace(yaml, "processing:\n  mode: \"remoteSsh\"\n  remoteSsh:\n    host: \"\"\n");

        result.Should().Be("version: 1\r\nprocessing:\r\n  mode: \"remoteSsh\"\r\n  remoteSsh:\r\n    host: \"\"\r\n\r\n# next\r\ndatabase:\r\n  path: \"jobs.db\"\r\n");
    }

    [Fact]
    public void ReplaceHandlesTheSectionAtTheEndOfTheFileWithAndWithoutATrailingNewline()
    {
        ProcessingBlock.Replace("version: 1\nprocessing:\n  mode: \"local\"\n", "processing:\n  mode: \"x\"\n")
            .Should().Be("version: 1\nprocessing:\n  mode: \"x\"\n");
        ProcessingBlock.Replace("version: 1\nprocessing:\n  mode: \"local\"", "processing:\n  mode: \"x\"\n")
            .Should().Be("version: 1\nprocessing:\n  mode: \"x\"\n");
    }

    [Fact]
    public void ReplaceKeepsAnIndentedCommentThatBelongsToAnotherKeyAndOnlyTrimsTheTail()
    {
        const string yaml = "processing:\n  # inside\n  mode: \"local\"\n  # trailing inside\n\n# outside\nwatch:\n  roots: []\n";

        var result = ProcessingBlock.Replace(yaml, "processing:\n  mode: \"x\"\n");

        result.Should().Be("processing:\n  mode: \"x\"\n\n# outside\nwatch:\n  roots: []\n");
    }

    [Theory]
    [InlineData("version: 1\n", "version: 1\n\nprocessing:\n  mode: \"x\"\n")]
    [InlineData("version: 1", "version: 1\n\nprocessing:\n  mode: \"x\"\n")]
    [InlineData("version: 1\n\n", "version: 1\n\nprocessing:\n  mode: \"x\"\n")]
    [InlineData("", "processing:\n  mode: \"x\"\n")]
    public void ReplaceAppendsAfterABlankLineWhenThereIsNoProcessingKey(string yaml, string expected)
    {
        ProcessingBlock.Replace(yaml, "processing:\n  mode: \"x\"\n").Should().Be(expected);
    }

    [Fact]
    public void ReplaceIgnoresIndentedOrLookalikeProcessingKeys()
    {
        const string yaml = "other:\n  processing:\n    mode: nested\nprocessingTime: 5\n";

        var result = ProcessingBlock.Replace(yaml, "processing:\n  mode: \"x\"\n");

        result.Should().StartWith(yaml).And.EndWith("processing:\n  mode: \"x\"\n");
    }

    private static ProcessingSettings Parse(ProcessingSettings settings) =>
        YamlConfigurationLoader.ParseUnresolved("version: 1\n" + ProcessingBlock.Render(settings)).Processing;
}
