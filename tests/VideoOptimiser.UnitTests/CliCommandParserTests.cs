using FluentAssertions;
using VideoOptimiser.Application.Configuration;

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

    [Theory]
    [InlineData("--local", "queue", "run")]
    [InlineData("--remote", "queue", "run")]
    [InlineData("--local", "process", "movie.mp4")]
    [InlineData("--remote", "immich", "run", "--first")]
    public void RunCommandsTakeAModeOverride(string flag, params string[] args)
    {
        var command = CliCommand.Parse([.. args, flag]);

        command.IsValid.Should().BeTrue(command.Error);
        command.ModeOverride.Should().Be(flag == "--local" ? ProcessingModeOverride.Local : ProcessingModeOverride.Remote);
    }

    [Fact]
    public void ARunWithoutAFlagHasNoOverride() =>
        CliCommand.Parse(["queue", "run"]).ModeOverride.Should().BeNull();

    [Fact]
    public void LocalAndRemoteTogetherAreRejected()
    {
        var command = CliCommand.Parse(["queue", "run", "--local", "--remote"]);

        command.IsValid.Should().BeFalse();
        command.Error.Should().Be("--local and --remote cannot be used together.");
    }

    [Theory]
    [InlineData("--local", "queue", "discover")]
    [InlineData("--remote", "worker", "create")]
    [InlineData("--local", "doctor")]
    [InlineData("--remote", "finalize", "--ready")]
    [InlineData("--local", "immich", "finalize")]
    public void AModeOverrideOnAnyOtherCommandIsRejected(string flag, params string[] args)
    {
        var command = CliCommand.Parse([.. args, flag]);

        command.IsValid.Should().BeFalse();
        command.Error.Should().Be($"{flag} is only supported by queue run, process and immich run.");
    }

    [Fact]
    public void HelpListsTheImmichCommands()
    {
        CliCommand.Parse(["--help"]).ShowHelp.Should().BeTrue();
        CliApplication.Usage.Should()
            .Contain("video-optimiser immich run [--first] [--config <path>] [--local | --remote]")
            .And.Contain("video-optimiser immich finalize [--config <path>]");
    }
}
