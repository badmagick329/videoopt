using FluentAssertions;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Infrastructure.Features.Run;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// The detached stage protocol, behind a fake transport (<see cref="StageShell"/>) that sees each command as written, before
/// it is quoted for <c>bash -lc</c>. What the session makes of a finished stage (the CRF, the durations, the encode's output
/// check, the workspace removal) is in <see cref="RemoteSshProcessingSessionTests"/>.
/// </summary>
public sealed class RemoteStageRunnerTests : IDisposable
{
    /// <summary>The setting's default, <c>30m</c>.</summary>
    private static readonly TimeSpan StageSilenceTimeout = TimeSpan.FromMinutes(30);

    private static readonly IReadOnlyList<string> Arguments = ["crf-search", "--input", "/w/source.mkv"];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Stage.{Guid.NewGuid():N}");
    private readonly Guid _id = Guid.NewGuid();

    public RemoteStageRunnerTests() => Directory.CreateDirectory(_directory);

    /// <summary>The local original; the runner keeps each stage's log beside it.</summary>
    private string Source => Path.Combine(_directory, "source.mkv");

    private string Workspace => $"/var/tmp/video-optimiser/{_id:N}";
    private string CrfDirectory => $"{Workspace}/crf";
    private string EncodeDirectory => $"{Workspace}/encode-1";

    /// <summary>How many times the runner asked for the workspace to go.</summary>
    private int Removals { get; set; }

    /// <summary>The transport resends a launch whose reply was lost, after it ran; the resend must not start a second script,
    /// and the stage reads as missing until the first one writes its pid.</summary>
    [Fact]
    public async Task AResentLaunchAfterALostReplyStartsNoSecondScript()
    {
        var shell = new StageShell(["missing", "missing", "running", "completed 0"]) { ResendFirstLaunch = true };

        var run = await CreateRunner(shell).RunAsync(CrfDirectory, "CRF search", Arguments, Source, null, default);

        run.Log.Should().Be("crf 42 successful\n");
        shell.Launches.Should().HaveCount(2);
        shell.Launches[1].Should().Be(shell.Launches[0]);
        shell.ScriptsStarted.Should().Be(1);
    }

    /// <summary>A script that died without an exit code (a reboot, an OOM kill) leaves its marker behind; the next command
    /// must still launch the stage again.</summary>
    [Fact]
    public async Task AStageWhoseScriptDiedLeavingItsMarkerIsLaunchedAgainByTheNextCommand()
    {
        var shell = new StageShell(["missing", "missing", "missing", "missing", "missing", "missing", "running", "completed 0"]);

        var died = async () => await CreateRunner(shell).RunAsync(CrfDirectory, "CRF search", Arguments, Source, null, default);
        await died.Should().ThrowAsync<InvalidOperationException>().WithMessage("Remote stage has neither*");
        var run = await CreateRunner(shell).RunAsync(CrfDirectory, "CRF search", Arguments, Source, null, default);

        run.Log.Should().Be("crf 42 successful\n");
        shell.Launches.Should().HaveCount(2);
        shell.ScriptsStarted.Should().Be(2);
    }

    [Fact]
    public async Task FreshStageUsesWritableCacheInsideItsGuidWorkspace()
    {
        var shell = new StageShell(["missing", "completed 0"], log: string.Empty);

        _ = await CreateRunner(shell).RunAsync(CrfDirectory, "CRF search", Arguments, Source, null, default);

        var script = shell.Scripts.Single();
        script.Should().StartWith("#!/usr/bin/env bash");
        var expectedCache = $"{CrfDirectory}/cache";
        script.Should().Contain($"mkdir -p -- '{expectedCache}'");
        script.Should().Contain($"export XDG_CACHE_HOME='{expectedCache}'");
        expectedCache.StartsWith(Workspace + "/", StringComparison.Ordinal).Should().BeTrue();
        script.IndexOf("export XDG_CACHE_HOME", StringComparison.Ordinal).Should().BeLessThan(script.IndexOf("'/usr/bin/time'", StringComparison.Ordinal));

        // Backgrounding the whole `cd && nohup …` list keeps a subshell on the SSH channel until ab-av1 exits, which
        // turns live progress into one dump at the end of each stage (VOPT-41). Only the braced script may be backgrounded.
        var launch = shell.Launches.Single();
        launch.Should().Contain("{ nohup setsid bash ");
        launch.Should().Contain("</dev/null >/dev/null 2>&1 & }");
        launch.Should().StartWith($"cd -- '{CrfDirectory}' && ");
        RemoteStageRunner.BuildLaunchCommand("/w/a b", "/w/a b/run.sh", "launch-1").Should().Be("cd -- '/w/a b' && if mkdir -- 'launch-1'; then { nohup setsid bash '/w/a b/run.sh' </dev/null >/dev/null 2>&1 & }; elif ! test -d 'launch-1'; then exit 1; fi");
    }

    [Fact]
    public async Task AnotherFailedRemoteStageReportsTheErrorLineOfItsLog()
    {
        const string log = "[2026-10-13T10:55:18Z INFO  ab_av1::command::crf_search] starting\nError: ffmpeg exited with status 1\nCommand exited with non-zero status 1\n\tExit status: 1\n";
        var shell = new StageShell(["completed 1"], log);

        var action = async () => await CreateRunner(shell).RunAsync(CrfDirectory, "CRF search", Arguments, Source, null, default);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Remote processing stage failed with exit code 1: ffmpeg exited with status 1");
    }

    /// <summary>
    /// ab-av1 can go minutes without a line during a CRF search, so the stage's polls say the worker answered, at most once
    /// per <see cref="RemoteStageRunner.WorkerAnsweredInterval"/>: each is a line in the event file the TUI rereads.
    /// </summary>
    [Fact]
    public async Task AQuietStagesPollsSayTheWorkerAnsweredAtMostEveryInterval()
    {
        var clock = new ManualClock { TickLength = TimeSpan.FromSeconds(10) };
        var shell = new StageShell([.. Enumerable.Repeat<WorkerStep>("running", 7), "completed 1"], "Error: ffmpeg exited with status 1\n");
        var progress = new ListProgress();
        // Each poll waits 10 s, as the real one does.
        var runner = CreateRunner(shell, clock.TickAsync, clock.Read);

        var action = async () => await runner.RunAsync(CrfDirectory, "CRF search", Arguments, Source, progress, default);

        await action.Should().ThrowAsync<InvalidOperationException>();
        // The first state read is at 0 s, then polls every 10 s to 70 s, when the stage has ended: answers at 30 s and 60 s.
        progress.Lines.Count(line => line.Kind == JobProgressKind.WorkerAnswered).Should().Be(2);
    }

    [Fact]
    public async Task CtrlCDuringAStageLeavesItRunningOnTheWorker()
    {
        var shell = new StageShell(["running"]);
        using var cancellation = new CancellationTokenSource();
        var runner = CreateRunner(shell, _ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        });

        var action = async () => await runner.RunAsync(EncodeDirectory, "encode", Arguments, Source, null, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        shell.Commands.Should().ContainSingle();
    }

    /// <summary>
    /// A rerun says what it found: a stage still running, which it follows until it completes, or one that ended while no command
    /// watched it. A stage whose log is already kept ended under the command that kept it, such as a rerun after a stopped
    /// download. Either way it reads the result without launching another script.
    /// </summary>
    [Theory]
    [InlineData("running", false, "Picked up the CRF search running on the worker.")]
    [InlineData("completed 0", false, "The CRF search on the worker had already finished.")]
    [InlineData("completed 0", true, null)]
    public async Task AReattachedStageSaysWhetherItIsStillRunningOrFinishedUnwatched(string state, bool logAlreadyKept, string? expected)
    {
        var shell = new StageShell(state == "running" ? [state, "completed 0"] : [state]);
        if (logAlreadyKept)
        {
            Directory.CreateDirectory(Path.Combine(_directory, ".video-optimiser"));
            await File.WriteAllTextAsync(Path.Combine(_directory, ".video-optimiser", $"{_id:N}.crf.remote.log"), "crf 42 successful\n");
        }
        var progress = new ListProgress();

        var run = await CreateRunner(shell).RunAsync(CrfDirectory, "CRF search", Arguments, Source, progress, default);

        Steps(progress).Should().Equal(expected is null ? [] : [expected]);
        run.Log.Should().Be("crf 42 successful\n");
        run.Reattached.Should().BeTrue();
        shell.ScriptsStarted.Should().Be(0);
    }

    /// <summary>
    /// The worker's log already holds what an earlier command showed. Replaying it printed a finished encode's first progress
    /// line again, measured the time left from it, and reported a duration of the reattach alone (VOPT-72). Each poll also read
    /// the whole log, so a long encode cost more with every line.
    /// </summary>
    [Fact]
    public async Task AReattachedEncodeShowsOnlyWhatIsNewAndNamesNoDurationItCannotKnow()
    {
        const string before = "[2026-10-13T10:06:43Z INFO  ab_av1::command::encode] encoding movie.mkv\n[2026-10-13T10:06:59Z INFO  ab_av1::command::encode] 20%, 54 fps, eta 3 minutes\n[2026-10-13T10:07:15Z INFO  ab_av1::command::encode] 42%, 57 fps, eta 2 minutes\n";
        const string progressLine = "[2026-10-13T10:07:31Z INFO  ab_av1::command::encode] 60%, 58 fps, eta 1 minute\n";
        const string encoded = "Encoded 99.8 MiB (29%)\n";
        var shell = new StageShell([new("running"), new("running", progressLine), new("completed 0", encoded)], before);
        var progress = new ListProgress();
        var translator = new AbAv1OutputTranslator(AbAv1Stage.Encode, 95, 80, () => TimeSpan.Zero).Wrap(progress);

        _ = await CreateRunner(shell).RunAsync(EncodeDirectory, "encode", Arguments, Source, translator, default);

        progress.Lines.Should().Equal(
            JobProgress.Step("Picked up the encode running on the worker."),
            JobProgress.Reading("60%, 58 fps", 60),
            JobProgress.Step("Encoded 99.8 MiB (29% of the original)."));
        var beforeBytes = Encoding.UTF8.GetByteCount(before);
        shell.Polls.Should().Equal([(-1, 0), (beforeBytes, Encoding.UTF8.GetByteCount(progressLine)), (beforeBytes + Encoding.UTF8.GetByteCount(progressLine), Encoding.UTF8.GetByteCount(encoded))],
            "after joining at the end of the log each poll asks only for the bytes past the last one it read");
        shell.WholeLogReads.Should().Be(1, "the whole log is read once, when the stage has ended");
    }

    /// <summary>A poll can land in the middle of a line ab-av1 is writing; the half is kept for the next poll, not printed as a line.</summary>
    [Fact]
    public async Task ALineStillBeingWrittenIsReportedWholeOnTheNextPoll()
    {
        const string start = "[2026-10-13T10:06:59Z INFO  ab_av1::command::encode] 20%, 54 fps, e";
        const string rest = "ta 2 minutes\n";
        var shell = new StageShell([new("running"), new("running", start), new("running", rest), new("completed 0")], log: string.Empty);
        var progress = new ListProgress();

        _ = await CreateRunner(shell).RunAsync(EncodeDirectory, "encode", Arguments, Source, progress, default);

        progress.Lines.Where(line => line.Kind == JobProgressKind.ToolOutput).Select(line => line.Text).Should().Equal(start + rest.TrimEnd('\n'));
    }

    /// <summary>
    /// A stage whose process is alive but whose log has gone quiet for longer than <c>stageSilenceTimeout</c> (30 minutes by
    /// default) and 1.5 times its age at the last write is hung (VOPT-153): it is stopped on the worker, its log kept, its
    /// workspace removed, and the job fails. Rows: early on, past the setting; no log yet, timed from the stage's start; late
    /// on, past the scaled limit.
    /// </summary>
    [Theory]
    [InlineData(60, 1801, "30m 01s")]
    [InlineData(null, 1801, "30m 01s")]
    [InlineData(7200, 10801, "3h 00m")]
    public async Task AStageSilentPastItsLimitIsStoppedKeptAndFailedAsStalled(int? lastWriteSeconds, int silentSeconds, string silence)
    {
        var shell = new StageShell(["missing", RunningFor(Seconds(lastWriteSeconds), TimeSpan.FromSeconds(silentSeconds))], log: "stuck on test clip 2\n");

        var action = async () => await CreateRunner(shell).RunAsync(CrfDirectory, "CRF search", Arguments, Source, null, default);

        var thrown = (await action.Should().ThrowAsync<RemoteStageStalledException>()).Which;
        thrown.Message.Should().Be($"The CRF search on the worker wrote nothing to its log for {silence}, so it was stopped.");
        shell.Stops.Should().ContainSingle().Which.Should().Contain($"s='{CrfDirectory}'").And.Contain($"'{Workspace}'").And.Contain("kill -s TERM");
        (await File.ReadAllTextAsync(thrown.LogPath!)).Should().Be("stuck on test clip 2\n");
        Removals.Should().Be(1);
    }

    /// <summary>
    /// A healthy step's log goes quiet for at most as long as the stage has run, so a long encode is allowed a long silence.
    /// Rows: at the setting; 40 minutes silent two hours in (past the setting, inside 1.5 times the age); at the scaled limit.
    /// </summary>
    [Theory]
    [InlineData(60, 1800)]
    [InlineData(7200, 2400)]
    [InlineData(7200, 10800)]
    public async Task AStageWithinItsLimitIsLeftRunning(int? lastWriteSeconds, int silentSeconds)
    {
        var shell = new StageShell(["missing", RunningFor(Seconds(lastWriteSeconds), TimeSpan.FromSeconds(silentSeconds)), "completed 0"]);

        var run = await CreateRunner(shell).RunAsync(CrfDirectory, "CRF search", Arguments, Source, null, default);

        run.Log.Should().Be("crf 42 successful\n");
        shell.Stops.Should().BeEmpty();
    }

    /// <summary>Hours of work whose log keeps growing is never taken for a hung stage, however long the stage has run.</summary>
    [Fact]
    public async Task AStageWhoseLogKeepsGrowingIsNeverStopped()
    {
        var polls = Enumerable.Range(1, 6).Select(hour => new WorkerStep("running", $"{hour}h in\n", $"times {StageStart + (hour * 3600) + 60} {StageStart + (hour * 3600)} {StageStart}"));
        var shell = new StageShell([new WorkerStep("missing"), .. polls, new WorkerStep("completed 0")], log: string.Empty);

        _ = await CreateRunner(shell).RunAsync(EncodeDirectory, "encode", Arguments, Source, null, default);

        shell.Stops.Should().BeEmpty();
    }

    /// <summary>A stage that wrote its exit code between the poll and the stop has finished, and goes on as finished.</summary>
    [Fact]
    public async Task AStageThatEndedJustBeforeItsStopIsNotFailed()
    {
        var shell = new StageShell(["missing", RunningFor(TimeSpan.FromMinutes(1), TimeSpan.FromHours(1)), "completed 0"]) { EndsBeforeStop = true };

        var run = await CreateRunner(shell).RunAsync(CrfDirectory, "CRF search", Arguments, Source, null, default);

        run.Log.Should().Be("crf 42 successful\n");
        shell.Stops.Should().ContainSingle();
        Removals.Should().Be(0);
    }

    /// <summary>A times line this cannot read gives no verdict: a stage is never stopped on a reading it cannot trust.</summary>
    [Theory]
    [InlineData("times 9000000 1000000")]
    [InlineData("times 9000000 soon 1000000")]
    public async Task AStageWhoseTimesCannotBeReadIsNeverStopped(string times)
    {
        var shell = new StageShell(["missing", new WorkerStep("running", Times: times), "completed 0"]);

        var run = await CreateRunner(shell).RunAsync(CrfDirectory, "CRF search", Arguments, Source, null, default);

        run.Log.Should().Be("crf 42 successful\n");
        shell.Stops.Should().BeEmpty();
    }

    private RemoteStageRunner CreateRunner(StageShell shell, Func<CancellationToken, Task>? pollDelay = null, Func<TimeSpan>? clock = null) =>
        new(shell, _id, Workspace, StageSilenceTimeout, pollDelay ?? (_ => Task.CompletedTask), clock ?? new ManualClock().Read, _ =>
        {
            Removals++;
            return Task.CompletedTask;
        });

    private static TimeSpan? Seconds(int? seconds) => seconds is { } value ? TimeSpan.FromSeconds(value) : null;

    private static string[] Steps(ListProgress progress) => [.. progress.Lines.Where(line => line.Kind == JobProgressKind.Step).Select(line => line.Text)];

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// What the stage's state check finds at one poll: its state, what ab-av1 had written to the log by then, and the
    /// <c>times</c> line the worker printed, if any.
    /// </summary>
    private sealed record WorkerStep(string Header, string Appended = "", string? Times = null)
    {
        public static implicit operator WorkerStep(string header) => new(header);
    }

    /// <summary>The stage's start, in seconds on the worker's clock; any value does, since only differences count.</summary>
    private const long StageStart = 1_000_000;

    /// <summary>
    /// A running stage's poll whose log was last written <paramref name="lastWrite"/> after <see cref="StageStart"/> (null: no
    /// log yet) and has been silent for <paramref name="silent"/> since.
    /// </summary>
    private static WorkerStep RunningFor(TimeSpan? lastWrite, TimeSpan silent)
    {
        var written = lastWrite is { } after ? StageStart + (long)after.TotalSeconds : (long?)null;
        var now = (written ?? StageStart) + (long)silent.TotalSeconds;
        return new WorkerStep("running", Times: $"times {now} {written} {StageStart}");
    }

    /// <summary>
    /// A worker for one stage, behind the transport: it keeps the launch markers it was asked to create, as <c>mkdir</c> would,
    /// and answers the state check as the remote command does: the log from the byte offset it was asked for, or from the end
    /// for -1, or from the start when the offset is past the end. A launch command without a marker starts a script every time
    /// it arrives.
    /// </summary>
    /// <param name="log">The log already on the worker before the first poll.</param>
    private sealed class StageShell(IEnumerable<WorkerStep> steps, string log = "crf 42 successful\n") : IRemoteShell
    {
        private static readonly Regex MarkerPattern = new(@"mkdir -- '([^']+)'", RegexOptions.CultureInvariant);
        private static readonly Regex OffsetPattern = new(@"off=(-?\d+);", RegexOptions.CultureInvariant);
        private readonly Queue<WorkerStep> _steps = new(steps);
        private readonly HashSet<string> _markers = [];
        private string _log = log;

        /// <summary>The first launch arrives twice, as the transport resends one that ran but lost its reply.</summary>
        public bool ResendFirstLaunch { get; set; }

        /// <summary>The stage wrote its exit code just before a stop arrived, so the stop finds it ended.</summary>
        public bool EndsBeforeStop { get; init; }

        public List<string> Commands { get; } = [];
        public List<string> Scripts { get; } = [];
        public List<string> Launches { get; } = [];
        public List<string> Stops { get; } = [];
        public int ScriptsStarted { get; private set; }

        /// <summary>For each state check, the offset it asked for and how many bytes of log it was sent.</summary>
        public List<(long Asked, int Sent)> Polls { get; } = [];

        public int WholeLogReads { get; private set; }

        public Task<ExternalProcessResult> SshAsync(string command, string? standardInput = null, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (standardInput is not null) Scripts.Add(standardInput);
            if (command.Contains("nohup setsid bash", StringComparison.Ordinal))
            {
                Launch(command);
                if (ResendFirstLaunch)
                {
                    ResendFirstLaunch = false;
                    Launch(command);
                }
                return OkAsync();
            }
            if (command.Contains("kill -0", StringComparison.Ordinal)) return OkAsync(Poll(command));
            if (command.Contains("stop_stage \"$s/pid\"", StringComparison.Ordinal))
            {
                Stops.Add(command);
                return OkAsync(EndsBeforeStop ? "ended" : "stopped");
            }
            if (command.Contains("cat '", StringComparison.Ordinal) && command.Contains("/log'", StringComparison.Ordinal))
            {
                WholeLogReads++;
                return OkAsync(_log);
            }
            return OkAsync();
        }

        private void Launch(string command)
        {
            Launches.Add(command);
            var marker = MarkerPattern.Match(command);
            if (!marker.Success || _markers.Add(marker.Groups[1].Value)) ScriptsStarted++;
        }

        private string Poll(string command)
        {
            var step = _steps.Dequeue();
            _log += step.Appended;
            var bytes = Encoding.UTF8.GetBytes(_log);
            var asked = long.Parse(OffsetPattern.Match(command).Groups[1].Value, CultureInfo.InvariantCulture);
            var start = asked < 0 ? bytes.Length : asked > bytes.Length ? 0 : (int)asked;
            Polls.Add((asked, bytes.Length - start));
            var times = step.Times is null ? string.Empty : $"\n{step.Times}\n";
            return $"{step.Header}\n{times}\u001e{start}\u001e{bytes.Length}\u001e{Encoding.UTF8.GetString(bytes[start..])}";
        }

        private static Task<ExternalProcessResult> OkAsync(string output = "") => Task.FromResult(new ExternalProcessResult(0, output, string.Empty));
    }
}
