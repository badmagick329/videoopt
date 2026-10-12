using FluentAssertions;

namespace VideoOptimiser.UnitTests;

public sealed class CliCommandParserTests
{
    [Theory]
    [InlineData(new[] { "immich", "run" }, false)]
    [InlineData(new[] { "immich", "run", "--first" }, true)]
    [InlineData(new[] { "--first", "immich", "run", "--config", "my.yaml" }, true)]
    public void ParsesImmichRun(string[] args, bool first)
    {
        var command = CliCommand.Parse(args);

        command.Kind.Should().Be(CliCommandKind.ImmichRun, command.Error);
        command.First.Should().Be(first);
    }

    [Fact]
    public void ParsesImmichFinalize()
    {
        var command = CliCommand.Parse(["immich", "finalize", "--config", "my.yaml"]);

        command.Kind.Should().Be(CliCommandKind.ImmichFinalize, command.Error);
        command.ConfigurationPath.Should().Be("my.yaml");
    }

    [Theory]
    [InlineData("immich")]
    [InlineData("immich", "discover")]
    [InlineData("immich", "run", "extra")]
    [InlineData("immich", "finalize", "--first")]
    [InlineData("immich", "finalize", "--ready")]
    [InlineData("immich", "run", "--json")]
    [InlineData("queue", "run", "--first")]
    public void RejectsMalformedImmichCommandsAndFlags(params string[] args) =>
        CliCommand.Parse(args).IsValid.Should().BeFalse();

    [Fact]
    public void QueueDiscoverStillAcceptsFirst() =>
        CliCommand.Parse(["queue", "discover", "--first"]).Kind.Should().Be(CliCommandKind.QueueDiscover);

    [Fact]
    public void HelpListsTheImmichCommands()
    {
        CliCommand.Parse(["--help"]).ShowHelp.Should().BeTrue();
        CliApplication.Usage.Should()
            .Contain("video-optimiser immich run [--first] [--config <path>]")
            .And.Contain("video-optimiser immich finalize [--config <path>]");
    }
}
