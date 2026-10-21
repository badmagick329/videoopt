using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Cli.Features.Tui;
using VideoOptimiser.UnitTests.Features.Queue;

namespace VideoOptimiser.UnitTests.Features.Tui;

/// <summary>
/// How a command line reaches the TUI, with the terminal swapped for a fake. A bare command with output redirected prints the
/// status card exactly as before; <c>StatusCommandTests.TheBareCommandPrintsTheCard</c> pins that line by line through the real
/// terminal check, which is false under the test host, and these pin that the choice is the terminal's alone.
/// </summary>
public sealed class TuiEntryTests : QueueTestBase
{
    private readonly FakeScreen _screen = new();

    protected override void RegisterFakes(IServiceCollection services) => services.AddSingleton<ITuiScreen>(_screen);

    [Fact]
    public async Task ABareCommandInATerminalOpensTheTuiAndPrintsNothing()
    {
        await QueuedAsync("clip-a.mp4");
        _screen.IsAvailable = true;

        var result = await RunAsync();

        result.Should().Be((0, "", ""));
        _screen.Opened.Should().ContainSingle().Which.Path.Should().Be(Config);
        _screen.Opened[0].Database.Should().Be(DatabasePath);
    }

    /// <summary>With stdin or stdout redirected there is no screen to draw on, so a script still gets the card.</summary>
    [Fact]
    public async Task ABareCommandWithoutATerminalPrintsTheStatusCardAndNeverOpensTheTui()
    {
        await QueuedAsync("clip-a.mp4");
        _screen.IsAvailable = false;

        var result = await RunAsync();

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().StartWith($"Queue: 1 waiting.{Environment.NewLine}");
        _screen.Opened.Should().BeEmpty();
    }

    /// <summary>Only the bare command opens it: a typed <c>status</c> or <c>--json</c> is a request for the card, even in a terminal.</summary>
    [Theory]
    [InlineData("status")]
    [InlineData("status", "--json")]
    [InlineData("--json")]
    [InlineData("queue", "list")]
    public async Task AnythingButTheBareCommandNeverOpensTheTuiEvenInATerminal(params string[] args)
    {
        _screen.IsAvailable = true;

        var result = await RunAsync(args);

        result.ExitCode.Should().Be(0);
        _screen.Opened.Should().BeEmpty();
    }

    /// <summary>The bare command's own error for a missing configuration stands, in a terminal too: there are no settings to read a screen from.</summary>
    [Fact]
    public async Task ABareCommandWithNoConfigurationKeepsItsErrorInATerminal()
    {
        _screen.IsAvailable = true;
        var missing = Path.Combine(Workspace, "missing.yaml");

        var result = await ConsoleCapture.RunCliAsync(RegisterFakes, "--config", missing);

        result.ExitCode.Should().Be(3);
        result.Stderr.Should().Contain("To create one, run 'video-optimiser config init");
        _screen.Opened.Should().BeEmpty();
    }

    // ---- the hidden snapshot verb -----------------------------------------------------------

    /// <summary>One plain frame on stdout, as tall as asked: what a person or agent reads to check the screen from a shell.</summary>
    [Fact]
    public async Task TheSnapshotVerbPrintsOnePlainFrameOfTheRealQueueAtTheSizeAsked()
    {
        await QueuedAsync("clip-a.mp4");
        await QueuedAsync("clip-b.mp4");

        var result = await RunAsync("tui", "--snapshot", "--size", "140x30");

        result.ExitCode.Should().Be(0);
        result.Stderr.Should().BeEmpty();
        var rows = result.Stdout.TrimEnd().Split(Environment.NewLine);
        rows.Should().HaveCount(30);
        rows.Should().NotContain(row => row.Contains('\u001b', StringComparison.Ordinal));
        rows[0].Should().Contain("VideoOptimiser").And.Contain("jobs.db").And.Contain("2 in the queue");
        rows.Should().Contain(row => row.Contains("1  clip-a.mp4", StringComparison.Ordinal)).And.Contain(row => row.Contains("2  clip-b.mp4", StringComparison.Ordinal));
        rows.Should().Contain(row => row.Contains("No managed worker: remoteSsh.lifecycle is \"manual\".", StringComparison.Ordinal));
        rows.Should().Contain(row => row.Contains("No run is going.", StringComparison.Ordinal));
        _screen.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSnapshotVerbDefaultsToAFrameOf140By40()
    {
        await WriteConfigAsync();

        var result = await RunAsync("tui", "--snapshot");

        var rows = result.Stdout.TrimEnd().Split(Environment.NewLine);
        rows.Should().HaveCount(40);
        rows.Max(row => row.Length).Should().Be(140);
    }

    [Fact]
    public async Task SnapshotKeysOpenThePathDialogWithoutAddingAnything()
    {
        var result = await RunAsync("tui", "--snapshot", "--keys", "a,down,down,enter");

        result.ExitCode.Should().Be(0);
        result.Stderr.Should().BeEmpty();
        result.Stdout.Should().Contain("File path");
        result.Stdout.Should().Contain("enter");
        (await Repository.ListAsync(DatabasePath, terminal: false)).Should().BeEmpty();
    }

    [Fact]
    public void SnapshotLettersCarryTheKeyCodeUsedByConfirmations()
    {
        var command = CliCommand.Parse(["tui", "--snapshot", "--keys", "a,enter,y"]);

        command.IsValid.Should().BeTrue();
        var answer = command.SnapshotKeys![^1];
        answer.Key.Should().Be(ConsoleKey.Y);
        answer.KeyChar.Should().Be('y');
        answer.Modifiers.Should().Be((ConsoleModifiers)0);
    }

    [Theory]
    [InlineData("a,unknown")]
    [InlineData("a,,enter")]
    public void InvalidSnapshotKeysAreRefusedBeforeBuildingTheHost(string keys)
    {
        var command = CliCommand.Parse(["tui", "--snapshot", "--keys", keys]);

        command.IsValid.Should().BeFalse();
        command.Error.Should().StartWith("--keys requires");
    }

    [Fact]
    public void SnapshotKeysCannotBeUsedOnOtherCommands()
    {
        var command = CliCommand.Parse(["queue", "add", "--keys", "a"]);

        command.IsValid.Should().BeFalse();
        command.Error.Should().Be("--keys is only supported by tui.");
    }

    private sealed class FakeScreen : ITuiScreen
    {
        public bool IsAvailable { get; set; }
        public List<(string Path, string Database)> Opened { get; } = [];

        public Task<int> RunAsync(LoadedConfiguration configuration, CancellationToken cancellationToken)
        {
            Opened.Add((configuration.Path, configuration.Settings.Database.Path));
            return Task.FromResult(0);
        }
    }
}
