using FluentAssertions;
using VideoOptimiser.Application.Features.Run;

namespace VideoOptimiser.UnitTests;

public sealed class CliCommandParserTests
{
    /// <summary><c>--limit</c> is taken as given and <c>--first</c> is <c>--limit 1</c>, on every command that takes them, the watch-folder scan included (VOPT-197).</summary>
    [Theory]
    [InlineData("ImmichRun", 1, "--first", "immich", "run", "--config", "my.yaml")]
    [InlineData("QueueAddImmich", 1, "queue", "add", "immich", "--first")]
    [InlineData("QueueAdd", 1, "queue", "add", "--first")]
    [InlineData("QueueAddImmich", 5, "queue", "add", "immich", "--limit", "5")]
    [InlineData("QueueAdd", 3, "queue", "add", "--limit", "3")]
    [InlineData("QueueAdd", null, "queue", "add")]
    public void TheLimitIsAsGivenAndFirstIsOne(string kind, int? limit, params string[] args)
    {
        var command = CliCommand.Parse(args);

        command.Kind.ToString().Should().Be(kind, command.Error);
        command.Limit.Should().Be(limit);
    }

    /// <summary>The word <c>immich</c> selects the Immich form; any other argument is a file, and a file named <c>immich</c> is given as <c>.\immich</c>.</summary>
    [Theory]
    [InlineData("QueueAddImmich", "immich")]
    [InlineData("QueueAddFile", @".\immich")]
    [InlineData("QueueAddFile", "clip.mp4")]
    [InlineData("QueueAddFile", "-clip.mp4")]
    [InlineData("QueueAddFile", "help")]
    public void QueueAddTakesTheWordImmichOrAFile(string kind, string argument)
    {
        var command = CliCommand.Parse(["queue", "add", argument]);

        command.Kind.ToString().Should().Be(kind, command.Error);
        command.FilePath.Should().Be(kind == "QueueAddFile" ? argument : null);
        command.JobId.Should().BeNull();
    }

    /// <summary>
    /// A token starting with <c>--</c> that no option claims is refused at parse time, before any lock, instead of becoming a
    /// file or job ID. The suggestion prefers an option this command accepts, and never names a hidden one to a public command.
    /// </summary>
    [Theory]
    [InlineData("Unknown option '--forse'. Did you mean '--force'?", "queue", "add", "--forse", "clip.mp4")]
    [InlineData("Unknown option '--redy'. Did you mean '--ready'?", "finalize", "--redy")]
    [InlineData("Unknown option '--frobnicate'.", "queue", "add", "--frobnicate", "clip.mp4")]
    [InlineData("Unknown option '--forst'. Did you mean '--force'?", "queue", "add", "clip.mp4", "--forst")]
    [InlineData("Unknown option '--forst'. Did you mean '--first'?", "queue", "add", "--forst")]
    [InlineData("Unknown option '--JSON'. Did you mean '--json'?", "status", "--JSON")]
    [InlineData("Unknown option '--hepl'. Did you mean '--help'?", "queue", "list", "--hepl")]
    [InlineData("Unknown option '--snapshat'.", "status", "--snapshat")]
    [InlineData("Unknown option '--snapshat'. Did you mean '--snapshot'?", "tui", "--snapshat")]
    [InlineData("Unknown option '--alll'. Did you mean '--all'?", "queue", "nope", "--alll")]
    [InlineData("Unknown option '--'.", "queue", "add", "--", "clip.mp4")]
    public void AnUnknownOptionIsAnErrorThatNamesIt(string error, params string[] args)
    {
        var command = CliCommand.Parse(args);

        command.IsValid.Should().BeFalse();
        command.Error.Should().Be(error);
    }

    /// <summary>The job ID is the word in the job ID's place, so the verb of <c>queue drop --all</c> is never read as one.</summary>
    [Theory]
    [InlineData("QueueDrop", null, true, "queue", "drop", "--all")]
    [InlineData("QueueDismiss", null, true, "queue", "dismiss", "--all", "--config", "my.yaml")]
    [InlineData("QueueDrop", "3f2a", false, "queue", "drop", "3f2a")]
    [InlineData("Finalize", "3f2a", false, "finalize", "3f2a", "--config", "my.yaml")]
    [InlineData("Finalize", null, false, "finalize", "--ready")]
    [InlineData("Validate", "3f2a", false, "--config", "my.yaml", "validate", "3f2a")]
    [InlineData("QueueRetry", "3f2a", false, "queue", "retry", "3f2a")]
    public void TheJobIdIsTakenFromItsPlace(string kind, string? jobId, bool all, params string[] args)
    {
        var command = CliCommand.Parse(args);

        command.Kind.ToString().Should().Be(kind, command.Error);
        command.JobId.Should().Be(jobId);
        command.All.Should().Be(all);
        command.FilePath.Should().BeNull();
    }

    /// <summary><c>--help</c> and <c>-h</c> work anywhere, even after a typo; the word <c>help</c> only as the first word.</summary>
    [Theory]
    [InlineData("help")]
    [InlineData("help", "queue", "add")]
    [InlineData("--config", "my.yaml", "help")]
    [InlineData("queue", "drop", "-h")]
    [InlineData("queue", "add", "--forse", "--help")]
    public void HelpIsShownWhereTheHelpSaysItIs(params string[] args)
    {
        CliCommand.Parse(args).ShowHelp.Should().BeTrue();
    }

    [Fact]
    public void TheWordHelpAfterACommandIsNotHelp()
    {
        CliCommand.Parse(["queue", "hold", "help"]).Should().Match<CliCommand>(command => command.Kind == CliCommandKind.QueueHold && command.JobId == "help");
        CliCommand.Parse(["status", "help"]).Error.Should().Be("Unknown or incomplete command.");
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
        CliCommand.Parse(["queue", "reject", "3f2a", "--json"]).Error.Should().Be("--json is only supported by doctor, queue list, status, history, calibrate and cost.");
        CliCommand.Usage.Should().Contain("video-optimiser queue reject <job-id> [--config <path>]");
    }

    /// <summary><c>queue dismiss</c> (VOPT-115) takes a job ID or <c>--all</c>, never both, as <c>queue drop</c> does.</summary>
    [Fact]
    public void QueueDismissTakesAJobIdOrAll()
    {
        var one = CliCommand.Parse(["queue", "dismiss", "3f2a"]);
        var all = CliCommand.Parse(["queue", "dismiss", "--all"]);

        (one.Kind, one.JobId, one.All).Should().Be((CliCommandKind.QueueDismiss, "3f2a", false));
        (all.Kind, all.All).Should().Be((CliCommandKind.QueueDismiss, true));
        CliCommand.Parse(["queue", "dismiss"]).Error.Should().Be("queue dismiss requires a job ID or --all.");
        CliCommand.Parse(["queue", "dismiss", "3f2a", "--all"]).Error.Should().Be("queue dismiss requires a job ID or --all.");
        CliCommand.Parse(["queue", "hold", "3f2a", "--all"]).Error.Should().Be("--all is only supported by queue drop and queue dismiss.");
        CliCommand.Usage.Should().Contain("video-optimiser queue dismiss <job-id> [--config <path>]").And.Contain("video-optimiser queue dismiss --all [--config <path>]");
    }

    /// <summary>
    /// Most guards are pinned by no CLI transcript, so each is one row with its exact message. A flag meant for another
    /// command, a bad value and a clash between two flags are all rejected before anything runs.
    /// </summary>
    [Theory]
    [InlineData("--limit requires a whole number of 1 or more.", "queue", "add", "--limit", "0")]
    [InlineData("--limit requires a whole number of 1 or more.", "immich", "run", "--limit", "five")]
    [InlineData("--limit requires a whole number of 1 or more.", "immich", "run", "--limit")]
    [InlineData("--first and --limit cannot be used together.", "queue", "add", "--first", "--limit", "2")]
    [InlineData("--limit is only supported by queue add, queue add immich and immich run.", "run", "--limit", "3")]
    [InlineData("--limit is only supported by queue add, queue add immich and immich run.", "queue", "add", "clip.mp4", "--limit", "3")]
    [InlineData("--quality requires archive, standard or compact.", "config", "init", "--quality", "high")]
    [InlineData("--quality requires archive, standard or compact.", "config", "init", "--quality")]
    [InlineData("--quality is only supported by config init.", "config", "validate", "--quality", "archive")]
    [InlineData("Unknown or incomplete command.", "immich", "run", "extra")]
    [InlineData("--first is only supported by queue add, queue add immich and immich run.", "immich", "finalize", "--first")]
    [InlineData("--ready is only supported by finalize.", "immich", "finalize", "--ready")]
    [InlineData("--json is only supported by doctor, queue list, status, history, calibrate and cost.", "immich", "run", "--json")]
    [InlineData("--force is only supported by queue add <file>.", "queue", "add", "--force")]
    [InlineData("--dry-run is only supported by cleanup.", "run", "--dry-run")]
    [InlineData("--local and --remote cannot be used together.", "run", "--local", "--remote")]
    [InlineData("--local is only supported by run and immich run.", "queue", "add", "--local")]
    [InlineData("--local is only supported by run and immich run.", "queue", "add", "immich", "--local")]
    [InlineData("--crf is no longer supported: run picks the CRF itself.", "run", "--crf", "30")]
    [InlineData("--now is only supported by run stop.", "run", "--now")]
    [InlineData("--undo is only supported by run stop.", "run", "--undo")]
    [InlineData("--now and --undo cannot be used together.", "run", "stop", "--now", "--undo")]
    [InlineData("--keep-worker is only supported by background start.", "run", "--keep-worker")]
    [InlineData("--keep-worker is only supported by background start.", "background", "runner", "--worker-fate", "keep", "--keep-worker")]
    [InlineData("--worker-fate is only supported by background runner.", "background", "start", "--worker-fate", "keep")]
    [InlineData("--worker-fate requires delete or keep.", "background", "runner", "--worker-fate")]
    [InlineData("--worker-fate requires delete or keep.", "background", "runner", "--worker-fate", "sell")]
    [InlineData("background runner requires --worker-fate delete or keep.", "background", "runner")]
    [InlineData("--existing-worker is only supported by background runner.", "background", "start", "--existing-worker")]
    [InlineData("--local and --remote cannot be used together.", "background", "start", "--local", "--remote")]
    [InlineData("--local is only supported by run and immich run.", "run", "stop", "--local")]
    [InlineData("--json is only supported by doctor, queue list, status, history, calibrate and cost.", "run", "stop", "--json")]
    [InlineData("finalize requires a job ID or --ready.", "finalize")]
    [InlineData("finalize --ready does not take a job ID.", "finalize", "3f2a", "--ready")]
    [InlineData("queue drop requires a job ID or --all.", "queue", "drop")]
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
    [InlineData("StopRun", false, false, null, null, false, "run", "stop")]
    [InlineData("StopRun", true, false, null, null, false, "run", "stop", "--now", "--config", "my.yaml")]
    [InlineData("BackgroundStart", false, false, null, null, false, "background", "start")]
    [InlineData("BackgroundStart", false, true, null, "--remote", false, "background", "start", "--keep-worker", "--remote")]
    [InlineData("BackgroundRunner", false, false, WorkerFateChoice.Delete, null, false, "background", "runner", "--worker-fate", "delete", "--config", "my.yaml")]
    [InlineData("BackgroundRunner", false, false, WorkerFateChoice.Keep, "--local", false, "background", "runner", "--worker-fate", "KEEP", "--local")]
    [InlineData("BackgroundRunner", false, false, WorkerFateChoice.Keep, "--remote", true, "background", "runner", "--worker-fate", "keep", "--remote", "--existing-worker")]
    public void TheStopAndBackgroundVerbsParse(string kind, bool stopNow, bool keepWorker, WorkerFateChoice? fate, string? modeFlag, bool existingWorker, params string[] args)
    {
        var command = CliCommand.Parse(args);

        command.IsValid.Should().BeTrue(command.Error);
        command.Kind.ToString().Should().Be(kind);
        command.StopNow.Should().Be(stopNow);
        command.KeepWorker.Should().Be(keepWorker);
        command.WorkerFate.Should().Be(fate);
        (command.ModeOverride?.Flag).Should().Be(modeFlag);
        command.ExistingWorker.Should().Be(existingWorker);
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
    [InlineData("--json is only supported by doctor, queue list, status, history, calibrate and cost.", "tui", "--snapshot", "--json")]
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
    public void HelpListsTheLimitedAddsAndTheImmichCommands()
    {
        CliCommand.Parse(["--help"]).ShowHelp.Should().BeTrue();
        CliCommand.Usage.Should()
            .Contain("video-optimiser queue add [--first | --limit <n>] [--config <path>]")
            .And.Contain("video-optimiser queue add immich [--first | --limit <n>] [--config <path>]")
            .And.Contain("video-optimiser immich run [--first | --limit <n>] [--config <path>] [--local | --remote]")
            .And.Contain("video-optimiser immich finalize [--config <path>]");
    }

    /// <summary>Users start a background run from the TUI, so the launcher and the runner stay out of the help.</summary>
    [Fact]
    public void HelpListsRunStopAndNotTheBackgroundVerbs()
    {
        CliCommand.Usage.Should().Contain("video-optimiser run stop [--now | --undo] [--config <path>]").And.NotContain("background");
    }
}
