using FluentAssertions;

namespace VideoOptimiser.UnitTests;

public sealed class CliCommandParserTests
{
    /// <summary>
    /// <c>--first</c> is <c>--limit 1</c> for <c>immich run</c>. The CLI transcripts only type <c>--limit 3</c> or no limit, so
    /// this is where the value 1 is pinned.
    /// </summary>
    [Fact]
    public void FirstOnImmichRunIsALimitOfOne()
    {
        var command = CliCommand.Parse(["--first", "immich", "run", "--config", "my.yaml"]);

        command.Kind.Should().Be(CliCommandKind.ImmichRun, command.Error);
        command.Limit.Should().Be(1);
    }

    [Fact]
    public void QueueAddStillAcceptsFirst()
    {
        var command = CliCommand.Parse(["queue", "add", "--first"]);

        command.Kind.Should().Be(CliCommandKind.QueueAdd);
        command.First.Should().BeTrue();
        command.Limit.Should().BeNull();
    }

    /// <summary>
    /// Most guards are pinned by no CLI transcript, so each is one row with its exact message. A flag meant for another
    /// command, a bad value and a clash between two flags are all rejected before anything runs.
    /// </summary>
    [Theory]
    [InlineData("--limit requires a whole number of 1 or more.", "immich", "run", "--limit", "0")]
    [InlineData("--limit requires a whole number of 1 or more.", "immich", "run", "--limit", "five")]
    [InlineData("--limit requires a whole number of 1 or more.", "immich", "run", "--limit")]
    [InlineData("--first and --limit cannot be used together.", "immich", "run", "--first", "--limit", "3")]
    [InlineData("--limit is only supported by immich run.", "run", "--limit", "3")]
    [InlineData("--quality requires archive, standard or compact.", "config", "init", "--quality", "high")]
    [InlineData("--quality requires archive, standard or compact.", "config", "init", "--quality")]
    [InlineData("--quality is only supported by config init.", "config", "validate", "--quality", "archive")]
    [InlineData("Unknown or incomplete command.", "immich", "run", "extra")]
    [InlineData("--first is only supported by queue add and immich run.", "immich", "finalize", "--first")]
    [InlineData("--ready is only supported by finalize.", "immich", "finalize", "--ready")]
    [InlineData("--json is only supported by doctor, queue list, status, history and calibrate.", "immich", "run", "--json")]
    [InlineData("--force is only supported by queue add <file>.", "queue", "add", "--force")]
    [InlineData("--dry-run is only supported by cleanup.", "run", "--dry-run")]
    [InlineData("--local and --remote cannot be used together.", "run", "--local", "--remote")]
    [InlineData("--local is only supported by run and immich run.", "queue", "add", "--local")]
    public void RejectsWithTheReason(string error, params string[] args)
    {
        var command = CliCommand.Parse(args);

        command.IsValid.Should().BeFalse();
        command.Error.Should().Be(error);
    }

    [Fact]
    public void HelpListsTheImmichCommands()
    {
        CliCommand.Parse(["--help"]).ShowHelp.Should().BeTrue();
        CliCommand.Usage.Should()
            .Contain("video-optimiser immich run [--first | --limit <n>] [--config <path>] [--local | --remote]")
            .And.Contain("video-optimiser immich finalize [--config <path>]");
    }
}
