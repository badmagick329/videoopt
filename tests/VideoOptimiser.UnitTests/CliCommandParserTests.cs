using FluentAssertions;
using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.UnitTests;

public sealed class CliCommandParserTests
{
    [Theory]
    [InlineData(new[] { "immich", "run" }, null)]
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
    [InlineData("immich", "run", "--limit", "0")]
    [InlineData("immich", "run", "--limit", "five")]
    [InlineData("immich", "run", "--limit")]
    public void LimitNeedsAPositiveWholeNumber(params string[] args)
    {
        var command = CliCommand.Parse(args);

        command.IsValid.Should().BeFalse();
        command.Error.Should().Be("--limit requires a whole number of 1 or more.");
    }

    [Fact]
    public void FirstAndLimitTogetherAreRejected() =>
        CliCommand.Parse(["immich", "run", "--first", "--limit", "3"]).Error.Should().Be("--first and --limit cannot be used together.");

    [Theory]
    [InlineData("queue", "run")]
    public void LimitOnAnyOtherCommandIsRejected(params string[] args) =>
        CliCommand.Parse([.. args, "--limit", "3"]).Error.Should().Be("--limit is only supported by immich run.");

    [Theory]
    [InlineData("archive", QualityLevel.Archive)]
    [InlineData("Standard", QualityLevel.Standard)]
    [InlineData("COMPACT", QualityLevel.Compact)]
    public void ConfigInitTakesAQualityLevel(string value, QualityLevel level)
    {
        var command = CliCommand.Parse(["config", "init", "--quality", value]);

        command.Kind.Should().Be(CliCommandKind.ConfigInit, command.Error);
        command.Quality.Should().Be(level);
    }

    [Fact]
    public void ConfigInitWithoutQualityLeavesItToTheQuestion() =>
        CliCommand.Parse(["config", "init"]).Quality.Should().BeNull();

    [Theory]
    [InlineData("config", "init", "--quality", "high")]
    [InlineData("config", "init", "--quality")]
    public void QualityNeedsALevel(params string[] args) =>
        CliCommand.Parse(args).Error.Should().Be("--quality requires archive, standard or compact.");

    [Theory]
    [InlineData("config", "validate")]
    [InlineData("immich", "run")]
    public void QualityOnAnyOtherCommandIsRejected(params string[] args) =>
        CliCommand.Parse([.. args, "--quality", "archive"]).Error.Should().Be("--quality is only supported by config init.");

    [Fact]
    public void ParsesImmichFinalize()
    {
        var command = CliCommand.Parse(["immich", "finalize", "--config", "my.yaml"]);

        command.Kind.Should().Be(CliCommandKind.ImmichFinalize, command.Error);
        command.ConfigurationPath.Should().Be("my.yaml");
    }

    [Theory]
    [InlineData("immich")]
    [InlineData("immich", "run", "extra")]
    [InlineData("immich", "finalize", "--first")]
    [InlineData("immich", "finalize", "--ready")]
    [InlineData("immich", "run", "--json")]
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
    [InlineData("--json")]
    [InlineData("--first")]
    public void RemoteSetupRejectsFlagsMeantForOtherCommands(string flag)
    {
        CliCommand.Parse(["remote", "setup", flag]).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("--local", "queue", "discover")]
    [InlineData("--remote", "worker", "create")]
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
}
