using FluentAssertions;
using VideoOptimiser.Application.Features.Run;

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

    /// <summary><c>queue reject</c> (VOPT-112) takes exactly a job ID, like <c>queue hold</c>; it has no <c>--all</c> or <c>--json</c>, and the usage lists it.</summary>
    [Fact]
    public void QueueRejectTakesAJobIdAndIsInTheUsage()
    {
        var command = CliCommand.Parse(["queue", "reject", "3f2a", "--config", "my.yaml"]);

        command.Kind.Should().Be(CliCommandKind.QueueReject, command.Error);
        command.JobId.Should().Be("3f2a");
        CliCommand.Parse(["queue", "reject"]).Error.Should().Be("Unknown or incomplete command.");
        CliCommand.Parse(["queue", "reject", "--all"]).Error.Should().Be("Unknown or incomplete command.");
        CliCommand.Parse(["queue", "reject", "3f2a", "--json"]).Error.Should().Be("--json is only supported by doctor, queue list, status, history and calibrate.");
        CliCommand.Usage.Should().Contain("video-optimiser queue reject <job-id> [--config <path>]");
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
    [InlineData("--now is only supported by run stop.", "run", "--now")]
    [InlineData("--keep-worker is only supported by background start.", "run", "--keep-worker")]
    [InlineData("--keep-worker is only supported by background start.", "background", "runner", "--worker-fate", "keep", "--keep-worker")]
    [InlineData("--worker-fate is only supported by background runner.", "background", "start", "--worker-fate", "keep")]
    [InlineData("--worker-fate requires delete or keep.", "background", "runner", "--worker-fate")]
    [InlineData("--worker-fate requires delete or keep.", "background", "runner", "--worker-fate", "sell")]
    [InlineData("background runner requires --worker-fate delete or keep.", "background", "runner")]
    [InlineData("--local and --remote cannot be used together.", "background", "start", "--local", "--remote")]
    [InlineData("--local is only supported by run and immich run.", "run", "stop", "--local")]
    [InlineData("--json is only supported by doctor, queue list, status, history and calibrate.", "run", "stop", "--json")]
    public void RejectsWithTheReason(string error, params string[] args)
    {
        var command = CliCommand.Parse(args);

        command.IsValid.Should().BeFalse();
        command.Error.Should().Be(error);
    }

    /// <summary>
    /// <c>run stop</c> is public; the two <c>background</c> verbs are the hidden launcher and the detached runner it starts, and
    /// the runner's arguments are exactly what the launcher builds.
    /// </summary>
    [Theory]
    [InlineData("StopRun", false, false, null, null, "run", "stop")]
    [InlineData("StopRun", true, false, null, null, "run", "stop", "--now", "--config", "my.yaml")]
    [InlineData("BackgroundStart", false, false, null, null, "background", "start")]
    [InlineData("BackgroundStart", false, true, null, "--remote", "background", "start", "--keep-worker", "--remote")]
    [InlineData("BackgroundRunner", false, false, WorkerFateChoice.Delete, null, "background", "runner", "--worker-fate", "delete", "--config", "my.yaml")]
    [InlineData("BackgroundRunner", false, false, WorkerFateChoice.Keep, "--local", "background", "runner", "--worker-fate", "KEEP", "--local")]
    public void TheStopAndBackgroundVerbsParse(string kind, bool stopNow, bool keepWorker, WorkerFateChoice? fate, string? modeFlag, params string[] args)
    {
        var command = CliCommand.Parse(args);

        command.IsValid.Should().BeTrue(command.Error);
        command.Kind.ToString().Should().Be(kind);
        command.StopNow.Should().Be(stopNow);
        command.KeepWorker.Should().Be(keepWorker);
        command.WorkerFate.Should().Be(fate);
        (command.ModeOverride?.Flag).Should().Be(modeFlag);
    }

    /// <summary><c>tui --snapshot</c> is the hidden way to see the TUI's screen from a shell; the size is optional and defaults where the command prints it.</summary>
    [Theory]
    [InlineData(null, null, "tui", "--snapshot")]
    [InlineData(80, 30, "tui", "--snapshot", "--size", "80x30")]
    [InlineData(140, 40, "--config", "my.yaml", "tui", "--size", "140X40", "--snapshot")]
    public void TheHiddenTuiSnapshotVerbParses(int? width, int? height, params string[] args)
    {
        var command = CliCommand.Parse(args);

        command.IsValid.Should().BeTrue(command.Error);
        command.Kind.Should().Be(CliCommandKind.TuiSnapshot);
        command.Bare.Should().BeFalse();
        (command.Size?.Width).Should().Be(width);
        (command.Size?.Height).Should().Be(height);
    }

    [Theory]
    [InlineData("tui requires --snapshot.", "tui")]
    [InlineData("--snapshot is only supported by tui.", "status", "--snapshot")]
    [InlineData("--snapshot is only supported by tui.", "--snapshot")]
    [InlineData("--size is only supported by tui.", "status", "--size", "80x30")]
    [InlineData("--size requires WIDTHxHEIGHT, such as 140x40.", "tui", "--snapshot", "--size")]
    [InlineData("--size requires WIDTHxHEIGHT, such as 140x40.", "tui", "--snapshot", "--size", "wide")]
    [InlineData("--size requires WIDTHxHEIGHT, such as 140x40.", "tui", "--snapshot", "--size", "0x40")]
    [InlineData("--size requires WIDTHxHEIGHT, such as 140x40.", "tui", "--snapshot", "--size", "80x-1")]
    [InlineData("--json is only supported by doctor, queue list, status, history and calibrate.", "tui", "--snapshot", "--json")]
    public void TheTuiSnapshotVerbRefusesWhatItCannotUse(string error, params string[] args)
    {
        var command = CliCommand.Parse(args);

        command.IsValid.Should().BeFalse();
        command.Error.Should().Be(error);
    }

    /// <summary>The bare command opens the TUI, so the snapshot verb that checks it from a shell stays out of the help, like the background verbs.</summary>
    [Fact]
    public void TheTuiSnapshotVerbIsHiddenFromHelp()
    {
        CliCommand.Usage.Should().NotContain("tui").And.NotContain("snapshot");
    }

    /// <summary>No verb is the status card, marked bare so a missing configuration can say how to create one; typed <c>status</c> is not.</summary>
    [Theory]
    [InlineData(true, null)]
    [InlineData(true, "my.yaml", "--config", "my.yaml")]
    [InlineData(false, null, "status")]
    public void NoVerbIsTheBareStatusCard(bool bare, string? config, params string[] args)
    {
        var command = CliCommand.Parse(args);

        command.IsValid.Should().BeTrue(command.Error);
        command.Kind.Should().Be(CliCommandKind.Status);
        command.Bare.Should().Be(bare);
        command.ConfigurationPath.Should().Be(config);
    }

    [Fact]
    public void UsageStartsWithTheBareCommand()
    {
        CliCommand.Usage.ReplaceLineEndings("|").Split('|')[..2].Should().Equal("Usage:", "  video-optimiser [--config <path>]");
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

    /// <summary>Users start a background run from the TUI, so the launcher and the runner stay out of the help.</summary>
    [Fact]
    public void HelpListsRunStopAndNotTheBackgroundVerbs()
    {
        CliCommand.Usage.Should().Contain("video-optimiser run stop [--now] [--config <path>]").And.NotContain("background");
    }
}
