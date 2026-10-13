using FluentAssertions;
using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.UnitTests;

public sealed class CliCommandParserTests
{
    [Theory]
    [InlineData(new[] { "immich", "run" }, null)]
    [InlineData(new[] { "immich", "run", "--first" }, 1)]
    [InlineData(new[] { "--first", "immich", "run", "--config", "my.yaml" }, 1)]
    [InlineData(new[] { "immich", "run", "--limit", "5" }, 5)]
    [InlineData(new[] { "--limit", "1", "immich", "run", "--remote" }, 1)]
    public void ParsesImmichRun(string[] args, int? limit)
    {
        var command = CliCommand.Parse(args);

        command.Kind.Should().Be(CliCommandKind.ImmichRun, command.Error);
        command.Limit.Should().Be(limit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("five")]
    [InlineData("2.5")]
    [InlineData("--remote")]
    public void LimitNeedsAPositiveWholeNumber(string value)
    {
        var command = CliCommand.Parse(["immich", "run", "--limit", value]);

        command.IsValid.Should().BeFalse();
        command.Error.Should().Be("--limit requires a whole number of 1 or more.");
    }

    [Fact]
    public void LimitWithoutAValueIsRejected() =>
        CliCommand.Parse(["immich", "run", "--limit"]).Error.Should().Be("--limit requires a whole number of 1 or more.");

    [Fact]
    public void FirstAndLimitTogetherAreRejected() =>
        CliCommand.Parse(["immich", "run", "--first", "--limit", "3"]).Error.Should().Be("--first and --limit cannot be used together.");

    [Theory]
    [InlineData("queue", "run")]
    [InlineData("queue", "discover")]
    [InlineData("immich", "finalize")]
    public void LimitOnAnyOtherCommandIsRejected(params string[] args) =>
        CliCommand.Parse([.. args, "--limit", "3"]).Error.Should().Be("--limit is only supported by immich run.");

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
    public void QueueDiscoverStillAcceptsFirst()
    {
        var command = CliCommand.Parse(["queue", "discover", "--first"]);

        command.Kind.Should().Be(CliCommandKind.QueueDiscover);
        command.First.Should().BeTrue();
        command.Limit.Should().BeNull();
    }

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

    [Fact]
    public void ParsesRemoteSetupWithAnOptionalConfig()
    {
        var command = CliCommand.Parse(["remote", "setup", "--config", "my.yaml"]);

        command.Kind.Should().Be(CliCommandKind.RemoteSetup, command.Error);
        command.ConfigurationPath.Should().Be("my.yaml");
        CliCommand.Parse(["remote", "setup"]).Kind.Should().Be(CliCommandKind.RemoteSetup);
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("setup")]
    public void RemoteSetupNeedsBothWords(string word)
    {
        CliCommand.Parse([word]).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("--local")]
    [InlineData("--remote")]
    [InlineData("--json")]
    [InlineData("--first")]
    public void RemoteSetupRejectsFlagsMeantForOtherCommands(string flag)
    {
        CliCommand.Parse(["remote", "setup", flag]).IsValid.Should().BeFalse();
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
            .Contain("video-optimiser immich run [--first | --limit <n>] [--config <path>] [--local | --remote]")
            .And.Contain("video-optimiser immich finalize [--config <path>]");
    }

    [Fact]
    public void HelpListsRemoteSetupAfterTheWorkerCommands()
    {
        const string deleteLine = "video-optimiser worker delete [--config <path>]";
        const string setupLine = "video-optimiser remote setup [--config <path>]";

        CliApplication.Usage.Should().Contain(setupLine);
        CliApplication.Usage.IndexOf(setupLine, StringComparison.Ordinal).Should().BeGreaterThan(CliApplication.Usage.IndexOf(deleteLine, StringComparison.Ordinal));
    }
}
