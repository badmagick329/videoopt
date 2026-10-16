using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Infrastructure.Features.Run;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.UnitTests.Features.Queue;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// What a person sees of the background runner through the real CLI: <c>run stop</c> in each state a run can be in, the hidden
/// launcher's outcomes (with a scripted launcher that plays the runner's part), and the event file and keep-awake hold that a
/// foreground <c>run</c> now has too. The run's own behaviour is <see cref="BackgroundRunTests"/>'; the refusal that names
/// <c>run stop</c> is in <see cref="RunLockTests"/>.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class BackgroundRunCliTests : QueueTestBase
{
    private readonly FakeKeepAwake _keepAwake = new();
    private readonly ScriptedLauncher _launcher = new() { Scripted = true };
    private readonly SqliteRunRequestStore _requests = new(new SqliteDatabaseInitializer());
    private DatabaseLock? _held;

    protected override void RegisterFakes(IServiceCollection services)
    {
        CliFakes.AcceptAnySettings(services);
        services.AddSingleton<IKeepAwake>(_keepAwake);
        services.AddSingleton<IDetachedLauncher>(_launcher);
        // A runner that never takes the lock must not hold the test for the launcher's real fifteen seconds.
        services.AddSingleton(provider => new BackgroundLaunch(provider.GetRequiredService<IDatabaseLocks>(), _launcher, TimeProvider.System, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public async Task RunStopWithNoRunGoingSaysSoAndSucceeds()
    {
        var result = await RunAsync("run", "stop");

        result.Should().Be((0, "No run is going." + Environment.NewLine, ""));
        (await _requests.ReadAsync(DatabasePath, CancellationToken.None)).StopAfterThisVideo.Should().BeFalse();
    }

    /// <summary>A run in a terminal never reads the requests row, so a request for it would be silently ignored: this says to use its Ctrl+C instead.</summary>
    [Fact]
    public async Task RunStopOfARunInATerminalRefusesAndLeavesNoRequest()
    {
        using var terminalRun = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), LockHolders.Run, out _);

        var result = await RunAsync("run", "stop");

        result.Should().Be((1, "", $"The run going now is in a terminal (process {Environment.ProcessId}): stop it with Ctrl+C in its window.{Environment.NewLine}"));
        (await _requests.ReadAsync(DatabasePath, CancellationToken.None)).Should().Be(new RunRequests(false, false, null));
    }

    [Theory]
    [InlineData(false, "Asked the run to stop after this video.", true, false)]
    [InlineData(true, "Asked the run to stop now.", false, true)]
    public async Task RunStopOfABackgroundRunAsksItAndSaysWhereToSeeItStop(bool now, string asked, bool afterThisVideo, bool stopNow)
    {
        using var backgroundRun = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), LockHolders.BackgroundRun, out _);

        var result = await RunAsync(now ? ["run", "stop", "--now"] : ["run", "stop"]);

        result.Should().Be((0, asked + Environment.NewLine + $"'video-optimiser status{ConfigArgument}' shows when it has stopped." + Environment.NewLine, ""));
        (await _requests.ReadAsync(DatabasePath, CancellationToken.None)).Should().Be(new RunRequests(afterThisVideo, stopNow, null));
    }

    /// <summary>The launcher answers only once the run lock shows the runner, so "started" means a run exists. The runner gets the fate and the full config path.</summary>
    [Theory]
    [InlineData(false, null, "delete")]
    [InlineData(true, "--remote", "keep")]
    public async Task StartingABackgroundRunWaitsForItsLockAndGivesTheRunnerItsArguments(bool keepWorker, string? mode, string fate)
    {
        _launcher.OnLaunch = () => _held = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), LockHolders.BackgroundRun, out _);
        string[] flags = [.. keepWorker ? ["--keep-worker"] : Array.Empty<string>(), .. mode is null ? Array.Empty<string>() : [mode]];

        (int ExitCode, string Stdout, string Stderr) result;
        try
        {
            result = await RunAsync(["background", "start", .. flags]);
        }
        finally
        {
            _held?.Dispose();
        }

        result.Should().Be((0, $"Started a background run (process {Environment.ProcessId})." + Environment.NewLine
            + $"'video-optimiser status{ConfigArgument}' shows how it is going; 'video-optimiser run stop{ConfigArgument}' stops it." + Environment.NewLine, ""));
        var launch = _launcher.Launches.Should().ContainSingle().Subject;
        launch.Arguments.Should().Equal(["background", "runner", "--worker-fate", fate, "--config", Path.GetFullPath(Config), .. mode is null ? Array.Empty<string>() : [mode]]);
        launch.WorkingDirectory.Should().Be(Environment.CurrentDirectory);
    }

    [Fact]
    public async Task StartingWhileARunIsGoingLaunchesNothingAndNamesTheHolder()
    {
        using var terminalRun = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), LockHolders.Run, out _);
        var holder = DatabaseLock.ReadLiveHolder(DatabaseLock.RunPath(DatabasePath))!;

        var result = await RunAsync("background", "start");

        result.Should().Be((1, "", $"Another run is using this database: video-optimiser run, started {CliLocks.FormatStarted(holder.StartedUtc)} (process {Environment.ProcessId})." + Environment.NewLine
            + "Wait for it to finish, or stop it with Ctrl+C in its window." + Environment.NewLine));
        _launcher.Launches.Should().BeEmpty();
    }

    [Fact]
    public async Task ARunnerThatEndsBeforeItHoldsTheLockIsReportedWithItsExitCode()
    {
        _launcher.ExitCode = 3;

        var result = await RunAsync("background", "start");

        result.Should().Be((1, "", "The background run quit before it could start (exit code 3)." + Environment.NewLine));
    }

    [Fact]
    public async Task ARunnerThatIsAliveButHoldsNoLockYetIsReportedAsNotStartedYet()
    {
        var result = await RunAsync("background", "start");

        result.Should().Be((1, "", $"The background run (process {Environment.ProcessId}) has not started yet. 'video-optimiser status{ConfigArgument}' shows whether it has." + Environment.NewLine));
    }

    /// <summary>
    /// A foreground run is a run like any other to a TUI in another window: it writes the event file from the moment it holds
    /// the lock to its summary, and keeps Windows awake exactly while it works.
    /// </summary>
    [Fact]
    public async Task AForegroundRunWritesTheEventFileAndKeepsWindowsAwakeWhileItWorks()
    {
        await QueuedAsync("clip-a.mp4");
        var heldWhileConverting = false;
        var processor = new ScriptedRunProcessor();
        processor.During[0] = _ =>
        {
            heldWhileConverting = _keepAwake.Held;
            return Task.CompletedTask;
        };

        var result = await RunAsync(services => services.AddSingleton<IJobProcessor>(processor), "run");

        result.ExitCode.Should().Be(0);
        heldWhileConverting.Should().BeTrue();
        _keepAwake.Log.Should().Equal("hold", "release");
        using var file = new RunEventFile();
        var events = file.Read(DatabasePath);
        events.Select(entry => entry.Kind).Should().ContainInOrder(RunEventKind.RunStarted, RunEventKind.ConversionStarted, RunEventKind.Progress, RunEventKind.ConversionEnded, RunEventKind.Summary);
        (events[0].Text, events[0].Count, events[0].Fate).Should().Be((LockHolders.Run, Environment.ProcessId, null));
        events[^1].Should().Match<RunEvent>(entry => entry.Kind == RunEventKind.Summary && entry.Ready == 1);
    }
}
