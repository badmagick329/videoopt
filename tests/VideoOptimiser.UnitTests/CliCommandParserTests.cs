using FluentAssertions;

namespace VideoOptimiser.UnitTests;

public sealed class CliCommandParserTests
{
    /// <summary><c>--first</c> is <c>--limit 1</c> for both Immich commands.</summary>
    [Theory]
    [InlineData("ImmichRun", "--first", "immich", "run", "--config", "my.yaml")]
    [InlineData("QueueAddImmich", "queue", "add", "immich", "--first")]
    public void FirstOnAnImmichCommandIsALimitOfOne(string kind, params string[] args)
    {
        var command = CliCommand.Parse(args);

        command.Kind.ToString().Should().Be(kind, command.Error);
        command.Limit.Should().Be(1);
    }

    /// <summary>The word <c>immich</c> selects the Immich form; any other argument is a file, and a file named <c>immich</c> is given as <c>.\immich</c>.</summary>
    [Theory]
    [InlineData("QueueAddImmich", "immich")]
    [InlineData("QueueAddFile", @".\immich")]
    [InlineData("QueueAddFile", "clip.mp4")]
    public void QueueAddTakesTheWordImmichOrAFile(string kind, string argument)
    {
        CliCommand.Parse(["queue", "add", argument]).Kind.ToString().Should().Be(kind);
    }

    [Fact]
    public void QueueAddImmichTakesALimit()
    {
        var command = CliCommand.Parse(["queue", "add", "immich", "--limit", "5"]);

        command.Kind.ToString().Should().Be("QueueAddImmich", command.Error);
        command.Limit.Should().Be(5);
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
    [InlineData("--limit is only supported by queue add immich and immich run.", "run", "--limit", "3")]
    [InlineData("--limit is only supported by queue add immich and immich run.", "queue", "add", "--limit", "3")]
    [InlineData("--quality requires archive, standard or compact.", "config", "init", "--quality", "high")]
    [InlineData("--quality requires archive, standard or compact.", "config", "init", "--quality")]
    [InlineData("--quality is only supported by config init.", "config", "validate", "--quality", "archive")]
    [InlineData("Unknown or incomplete command.", "immich", "run", "extra")]
    [InlineData("--first is only supported by queue add, queue add immich and immich run.", "immich", "finalize", "--first")]
    [InlineData("--ready is only supported by finalize.", "immich", "finalize", "--ready")]
    [InlineData("--json is only supported by doctor, queue list, status, history and calibrate.", "immich", "run", "--json")]
    [InlineData("--force is only supported by queue add <file>.", "queue", "add", "--force")]
    [InlineData("--dry-run is only supported by cleanup.", "run", "--dry-run")]
    [InlineData("--local and --remote cannot be used together.", "run", "--local", "--remote")]
    [InlineData("--local is only supported by run and immich run.", "queue", "add", "--local")]
    [InlineData("--local is only supported by run and immich run.", "queue", "add", "immich", "--local")]
    [InlineData("--crf is no longer supported: run picks the CRF itself.", "run", "--crf", "30")]
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
            .Contain("video-optimiser queue add immich [--first | --limit <n>] [--config <path>]")
            .And.Contain("video-optimiser immich run [--first | --limit <n>] [--config <path>] [--local | --remote]")
            .And.Contain("video-optimiser immich finalize [--config <path>]");
    }
}
