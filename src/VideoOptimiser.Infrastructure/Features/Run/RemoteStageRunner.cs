using System.Globalization;
using System.Text;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Tools;
using static VideoOptimiser.Infrastructure.Features.Run.RemoteShell;
using static VideoOptimiser.Infrastructure.Tools.PosixShell;

namespace VideoOptimiser.Infrastructure.Features.Run;

/// <summary>
/// Runs one job's ab-av1 stages (a CRF search, an encode attempt) detached on the worker, so Ctrl+C and a dropped connection
/// both leave a launched stage running there: launches a stage or reattaches to one an earlier command left, follows its
/// log, stops it when it stalls, and keeps its log once it has ended.
/// <para>It owns the stage's file layout (<see cref="PidFile"/> and the names after it): the stage script writes those files,
/// and the state check, the stop and the workspace sweep read them, all through the names here.</para>
/// </summary>
/// <param name="workspace">The job's workspace on the worker; every stage directory is inside it.</param>
/// <param name="stageSilenceTimeout">The shortest silence that can make a running stage stalled (see <see cref="WorkerTimes.StalledFor"/>).</param>
/// <param name="pollDelay">The wait between polls of a running stage.</param>
/// <param name="clock">Elapsed time, for deciding when the polls next say the worker answered; tests advance it by hand.</param>
/// <param name="removeWorkspace">Removes the job's workspace once a stage failed or stalled and its log is kept. What it throws is
/// reported and passed over (see <see cref="RemoveWorkspaceAsync"/>).</param>
internal sealed class RemoteStageRunner(IRemoteShell shell, Guid jobId, string workspace, TimeSpan stageSilenceTimeout, Func<CancellationToken, Task> pollDelay, Func<TimeSpan> clock, Func<CancellationToken, Task> removeWorkspace)
{
    /// <summary>
    /// The stage script's pid, written as the script starts. The script is a process-group leader (<c>setsid</c>), so the pid
    /// also names the group that takes ab-av1 and ffmpeg with it; its file time is the stage's start.
    /// </summary>
    internal const string PidFile = "pid";

    /// <summary>ab-av1's exit code, written only once ab-av1 has ended, so a stage still running has none.</summary>
    internal const string ExitCodeFile = "exit-code";

    /// <summary>ab-av1's whole output, stdout and stderr.</summary>
    internal const string LogFile = "log";

    /// <summary>ab-av1's cache (<c>XDG_CACHE_HOME</c>), inside the stage so it goes with the workspace.</summary>
    internal const string CacheDirectory = "cache";

    /// <summary>
    /// The line the stage script appends to <see cref="LogFile"/> when ab-av1 failed and the kernel's out-of-memory killer
    /// fired meanwhile (VOPT-107). The kernel usually kills FFmpeg, the biggest process, and ab-av1 then fails with an FFmpeg
    /// error that never names memory, so the script compares the kernel's <c>oom_kill</c> count (<c>/proc/vmstat</c>) from
    /// before and after ab-av1. The count is machine-wide, which is fine on a worker that runs nothing else.
    /// </summary>
    internal const string OutOfMemoryMarker = "video-optimiser: the kernel killed a process for lack of memory during this stage";

    /// <summary>The stage script of one launch; unique per launch, so its upload is safe to resend.</summary>
    internal static string ScriptName(string launchId) => $"run-{launchId}.sh";

    /// <summary>The launch's marker directory (see <see cref="BuildLaunchCommand"/>).</summary>
    internal static string MarkerName(string launchId) => $"launch-{launchId}";

    private static string InStage(string stageDirectory, string name) => $"{stageDirectory}/{name}";

    /// <param name="Log">The stage's whole log, read once it ended.</param>
    /// <param name="Reattached">The stage was already running, or had ended, when this command reached it.</param>
    internal sealed record StageRun(string Log, bool Reattached);

    /// <summary>
    /// Launches the stage, or reattaches to one an earlier command left behind: Ctrl+C and a dropped connection both leave
    /// a launched stage running on the worker.
    /// <para>The log is followed by byte offset, each poll fetching only what was added since the last. A reattach starts at
    /// the log's current end, so what an earlier command already showed is not shown again, and the progress and times the
    /// translator derives start at the reattach. The whole log is read once, when the stage has ended.</para>
    /// <para>A stage whose process is alive but whose log has gone quiet for too long is stopped and fails the job (VOPT-153):
    /// an ab-av1 or FFmpeg that hangs without exiting would otherwise be polled for ever while the worker bills, and each poll
    /// still says the worker answered. See <see cref="WorkerTimes.StalledFor"/> for the limit.</para>
    /// </summary>
    /// <param name="name">The stage as the reattach lines name it: <c>encode</c> or <c>CRF search</c>.</param>
    /// <param name="sourcePath">The local original; the stage's log is kept beside it.</param>
    /// <param name="interpretFailure">Turns the log of a stage that exited non-zero into a more specific exception than the generic one,
    /// or returns null to keep the generic one.</param>
    public async Task<StageRun> RunAsync(string stageDirectory, string name, IReadOnlyList<string> arguments, string sourcePath, IProgress<JobProgress>? progress, CancellationToken cancellationToken, Func<string, Exception?>? interpretFailure = null)
    {
        var state = await GetStageStateAsync(stageDirectory, FromTheEnd, progress, cancellationToken);
        var useCurrentState = true;
        var reattached = state.Running || state.ExitCode is not null;
        if (state.Running)
        {
            (progress as IReattachableProgress)?.StageWasAlreadyRunning();
            progress?.Report(JobProgress.Step($"Picked up the {name} running on the worker."));
        }
        // A stage whose log is already kept locally finished under an earlier command, which saw it end: a rerun after a
        // stopped download reaches here too, and has nothing new to say about the encode.
        else if (state.ExitCode is not null && !File.Exists(RetainedLogPath(stageDirectory, sourcePath)))
        {
            progress?.Report(JobProgress.Step($"The {name} on the worker had already finished."));
        }
        var offset = state.Offset;
        var startingPolls = 0;
        var lastAnswerReport = clock();
        if (state.ExitCode is null && !state.Running)
        {
            var launchId = Guid.NewGuid().ToString("N");
            var scriptPath = InStage(stageDirectory, ScriptName(launchId));
            var cacheDirectory = InStage(stageDirectory, CacheDirectory);
            var command = string.Join(" ", new[] { Quote("/usr/bin/time"), Quote("-v"), Quote("ab-av1") }.Concat(arguments.Select(Quote)));
            // The script runs in the stage directory, so it names its files bare. Each file is written whole and then
            // renamed, so a state check never reads half a pid or exit code. The out-of-memory marker goes into the log before
            // the exit code is written, since the log is read once the exit code exists.
            const string oomKills = "$(sed -n 's/^oom_kill //p' /proc/vmstat 2>/dev/null)";
            var script = $"#!/usr/bin/env bash\nset +e\numask 077\nmkdir -p -- {Quote(cacheDirectory)}\nexport XDG_CACHE_HOME={Quote(cacheDirectory)}\n"
                + $"printf '%s\\n' \"$$\" > {PidFile}.partial\nmv -f {PidFile}.partial {PidFile}\n"
                + $"oom_before={oomKills}\n{command} > {LogFile} 2>&1\ncode=$?\noom_after={oomKills}\n"
                + $"if [ \"$code\" -ne 0 ] && [ \"${{oom_after:-0}}\" -gt \"${{oom_before:-0}}\" ]; then printf '%s\\n' {Quote(OutOfMemoryMarker)} >> {LogFile}; fi\n"
                + $"printf '%s\\n' \"$code\" > {ExitCodeFile}.partial\nmv -f {ExitCodeFile}.partial {ExitCodeFile}\nexit 0\n";
            var create = await shell.SshAsync($"mkdir -p -- {Quote(stageDirectory)} && umask 077 && cat > {Quote(scriptPath)} && chmod 700 {Quote(scriptPath)}", script, progress: progress, cancellationToken: cancellationToken);
            EnsureSuccess(create, "Remote stage script upload");
            var launch = await shell.SshAsync(BuildLaunchCommand(stageDirectory, scriptPath, MarkerName(launchId)), progress: progress, cancellationToken: cancellationToken);
            EnsureSuccess(launch, "Remote stage launch");
            useCurrentState = false;
            startingPolls = StartingPolls;
            // The script's log starts empty.
            offset = 0;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!useCurrentState)
            {
                state = await GetStageStateAsync(stageDirectory, offset, progress, cancellationToken);
                // ab-av1 can go minutes without a line (one CRF test clip on a CPU worker), so the polls say the worker answered.
                if (clock() - lastAnswerReport >= WorkerAnsweredInterval)
                {
                    progress?.Report(JobProgress.WorkerAnswered);
                    lastAnswerReport = clock();
                }
            }
            useCurrentState = false;
            offset = ReportNewLines(state, progress);
            if (state.ExitCode is { } exitCode)
            {
                var (logPath, log) = await RetainLogAsync(stageDirectory, sourcePath, progress, cancellationToken);
                if (exitCode != 0)
                {
                    await RemoveWorkspaceAsync(progress);
                    // Kept in the stage's category: the fix is a bigger worker or --local, and queue retry is still the way back.
                    if (log.Contains(OutOfMemoryMarker, StringComparison.Ordinal))
                        throw new AbAv1StageException("The worker ran out of memory. Convert this video on a worker with more memory, or on this PC with --local.", logPath);
                    if (interpretFailure?.Invoke(log) is { } specific) throw specific;
                    var reason = AbAv1Output.LastErrorLine(log);
                    throw new AbAv1StageException($"Remote processing stage failed with exit code {exitCode}{(reason is null ? "." : $": {reason}")}", logPath);
                }
                return new StageRun(log, reattached);
            }
            if (state.Running && state.Times is { } times && times.StalledFor(stageSilenceTimeout) is { } silence
                && await StopStalledStageAsync(stageDirectory, progress, cancellationToken))
            {
                // Kept and cleaned up as a stage that failed is: a retry is a new job with a workspace of its own.
                var (logPath, _) = await RetainLogAsync(stageDirectory, sourcePath, progress, cancellationToken);
                await RemoveWorkspaceAsync(progress);
                throw new RemoteStageStalledException($"The {name} on the worker wrote nothing to its log for {HumanReadableValues.FormatDuration(silence)}, so it was stopped.", logPath);
            }
            if (state.Running) startingPolls = 0;
            // The launch returns before the script writes its pid, so a stage this call launched can read as missing at first.
            else if (startingPolls-- <= 0) throw new InvalidOperationException("Remote stage has neither a running process nor a completion marker.");
            await pollDelay(cancellationToken);
        }
    }

    /// <summary>
    /// Removes the workspace of a stage that failed or stalled, whose log is already kept. Best effort, so the stage's own error
    /// is the one the job fails with, not the removal's (VOPT-157). Not cancellable: a Ctrl+C after the stage ended should not
    /// leave the workspace behind.
    /// </summary>
    private Task RemoveWorkspaceAsync(IProgress<JobProgress>? progress) => WorkspaceCleanup.TryAsync(removeWorkspace, progress, CancellationToken.None);

    /// <summary>Whether the encode in <paramref name="stageDirectory"/> ended with exit 0 and left <paramref name="remoteOutput"/>.</summary>
    public async Task<bool> HasFinishedEncodeAsync(string stageDirectory, string remoteOutput, CancellationToken cancellationToken)
    {
        // Exit 1 is the answer no. The exit code file is written only once ab-av1 has ended, so a stage still running has none.
        var result = await shell.SshAsync($"test \"$(cat {Quote(InStage(stageDirectory, ExitCodeFile))} 2>/dev/null)\" = 0 && test -s {Quote(remoteOutput)}", cancellationToken: cancellationToken);
        if (result.ExitCode == 1) return false;
        EnsureSuccess(result, "Remote encode check");
        return true;
    }

    /// <summary>
    /// Reports the lines <paramref name="state"/> brought and returns the offset to poll from next. While the stage runs, a
    /// last line without its line break is left for the next poll rather than reported in two halves; once it has ended
    /// nothing more will come, so everything is reported.
    /// </summary>
    private static long ReportNewLines(StageState state, IProgress<JobProgress>? progress)
    {
        if (state.Chunk.Length == 0) return state.Offset;
        var complete = state.ExitCode is null ? state.Chunk.LastIndexOfAny(['\r', '\n']) + 1 : state.Chunk.Length;
        var text = state.Chunk[..complete];
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) progress?.Report(JobProgress.Tool(line));
        return state.Offset + Encoding.UTF8.GetByteCount(text);
    }

    /// <summary>
    /// How often a stage's polls report <see cref="JobProgress.WorkerAnswered"/>. Every report is a line in the run's event file,
    /// which the TUI rereads whole each second, so not every 10-second poll reports; half a minute is still well inside the
    /// minute after which the TUI calls a step quiet.
    /// </summary>
    internal static readonly TimeSpan WorkerAnsweredInterval = TimeSpan.FromSeconds(30);

    /// <summary>How many polls a stage this call launched may read as missing before it counts as dead.</summary>
    private const int StartingPolls = 3;

    /// <summary>The offset that asks for no log, only where it ends now.</summary>
    private const long FromTheEnd = -1;

    /// <summary>
    /// The braces put only the stage script in the background. Without them, <c>&amp;</c> backgrounds the whole
    /// <c>cd … &amp;&amp; nohup …</c> list as a subshell that inherits the SSH channel's output, so the launch's ssh stays
    /// open until ab-av1 exits and the poll loop then prints the whole stage log at once.
    /// <para>The marker makes the launch idempotent: <see cref="IRemoteShell.SshAsync"/> resends the same command after an exit
    /// 255, which can follow a launch that ran but lost its reply, and a second script would share the stage's log, pid,
    /// exit code and output. It is unique per launch, not a fixed lock, so a later command can still relaunch a stage
    /// whose script died without an exit code.</para>
    /// </summary>
    internal static string BuildLaunchCommand(string stageDirectory, string scriptPath, string marker) =>
        $"cd -- {Quote(stageDirectory)} && if mkdir -- {Quote(marker)}; then {{ nohup setsid bash {Quote(scriptPath)} </dev/null >/dev/null 2>&1 & }}; elif ! test -d {Quote(marker)}; then exit 1; fi";

    /// <param name="Offset">Where <paramref name="Chunk"/> starts in the log, in bytes.</param>
    /// <param name="Chunk">The log from <paramref name="Offset"/> to its end as it was read.</param>
    /// <param name="Times">The worker's clock and the stage's file times, or null when the worker did not print them all.</param>
    private sealed record StageState(bool Running, int? ExitCode, long Offset, string Chunk, WorkerTimes? Times = null);

    /// <summary>
    /// Three times off the worker's clock, in seconds since the epoch: now, the log's last write (null before the script made
    /// the log) and the stage's start (its pid file's, written as the script starts). Taken on the worker, not here, so a
    /// later command that reattaches to the stage has them too, and this PC's clock being off changes nothing.
    /// </summary>
    private sealed record WorkerTimes(long Now, long? LastLogWrite, long Started)
    {
        /// <summary>
        /// How long the log has been silent when that is longer than allowed, else null. ab-av1 writes each step's progress at
        /// 16, 32, 64, 128 s… after the step began, doubling without a cap, so a healthy step is never silent for longer than
        /// it has been running, at most the stage's age at its last write. Allowing half as much again leaves room; a stage
        /// younger than that is held to <paramref name="floor"/> instead (<c>stageSilenceTimeout</c>), which a CRF search, whose
        /// logger restarts with every test clip, never comes near.
        /// </summary>
        public TimeSpan? StalledFor(TimeSpan floor)
        {
            // A relaunch writes its pid before it empties the old log, so an older log time is the stage's start.
            var lastWrite = Math.Max(LastLogWrite ?? Started, Started);
            var silence = TimeSpan.FromSeconds(Now - lastWrite);
            var allowed = TimeSpan.FromSeconds(1.5 * (lastWrite - Started));
            return silence > (allowed > floor ? allowed : floor) ? silence : null;
        }
    }

    /// <summary>
    /// The stage's state and the part of its log from <paramref name="offset"/>, in one SSH call. The state is read before the
    /// log, so a stage that reads as ended has its final lines in the chunk. An offset past the log's end means ab-av1 started the
    /// log again, and it is read from the start. The state's line is followed by a <c>times</c> line for <see cref="WorkerTimes"/>.
    /// </summary>
    private async Task<StageState> GetStageStateAsync(string stageDirectory, long offset, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var exitCode = Quote(InStage(stageDirectory, ExitCodeFile));
        var pid = Quote(InStage(stageDirectory, PidFile));
        var log = Quote(InStage(stageDirectory, LogFile));
        var command = $"if test -s {exitCode}; then printf 'completed '; cat {exitCode}; elif test -s {pid} && kill -0 \"$(cat {pid})\" 2>/dev/null; then printf 'running\\n'; else printf 'missing\\n'; fi; "
            + $"printf '\\ntimes %s %s %s\\n' \"$(date +%s)\" \"$(stat -c %Y -- {log} 2>/dev/null)\" \"$(stat -c %Y -- {pid} 2>/dev/null)\"; printf '\\036'; "
            + $"n=0; test ! -f {log} || n=$(stat -c '%s' {log}); off={offset.ToString(CultureInfo.InvariantCulture)}; if test \"$off\" -lt 0; then off=$n; elif test \"$off\" -gt \"$n\"; then off=0; fi; "
            + $"printf '%s\\036%s\\036' \"$off\" \"$n\"; if test \"$n\" -gt \"$off\"; then tail -c +$((off+1)) {log} | head -c $((n-off)); fi";
        var result = await shell.SshAsync(command, progress: progress, cancellationToken: cancellationToken);
        var output = result.StandardOutput;
        var first = output.IndexOf('\x1e');
        var second = first < 0 ? -1 : output.IndexOf('\x1e', first + 1);
        var third = second < 0 ? -1 : output.IndexOf('\x1e', second + 1);
        if (third < 0 || !long.TryParse(output[(first + 1)..second], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
            throw new InvalidDataException("Remote stage state was invalid.");
        var lines = output[..first].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = lines.Length > 0 ? lines[0] : string.Empty;
        var times = lines.Skip(1).Select(ParseTimes).FirstOrDefault(parsed => parsed is not null);
        var chunk = output[(third + 1)..];
        if (header.StartsWith("completed ", StringComparison.Ordinal) && int.TryParse(header[10..].Trim(), CultureInfo.InvariantCulture, out var code)) return new StageState(false, code, start, chunk, times);
        return new StageState(string.Equals(header, "running", StringComparison.Ordinal), null, start, chunk, times);
    }

    /// <summary>
    /// Reads <c>times NOW LOG START</c>, where only LOG may be empty. Anything else is no times at all, and so no stall verdict:
    /// a stage is never stopped on a reading this cannot trust.
    /// </summary>
    private static WorkerTimes? ParseTimes(string line)
    {
        // Split on single spaces, so an empty field keeps its place.
        var parts = line.Split(' ');
        if (parts.Length != 4 || parts[0] != "times") return null;
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var now) || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var started)) return null;
        if (parts[2].Length == 0) return new WorkerTimes(now, null, started);
        return long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var logWrite) ? new WorkerTimes(now, logWrite, started) : null;
    }

    /// <summary>
    /// A shell function, <c>stop_stage PIDFILE WORKSPACE</c>, that ends the stage whose pid file it is given; one line, so it
    /// opens a one-line command as well as a script. The stage script is a process-group leader whose pid its stage directory
    /// keeps (<see cref="PidFile"/>), so the group takes ab-av1 and ffmpeg with it. The command line is checked first: a pid
    /// file that outlived its stage may name a pid that something else now holds. A missing or empty pid file stops nothing,
    /// and the function always succeeds, so a resend after a lost reply is harmless.
    /// </summary>
    internal const string StopStageFunction =
        "stop_stage() { pid=$(cat -- \"$1\" 2>/dev/null); case \"$pid\" in ''|*[!0-9]*) return 0;; esac; "
        + "if tr '\\0' ' ' < \"/proc/$pid/cmdline\" 2>/dev/null | grep -qF -- \"$2/\"; then "
        + "kill -s TERM -- \"-$pid\" 2>/dev/null; sleep 1; kill -s KILL -- \"-$pid\" 2>/dev/null; fi; return 0; }";

    /// <summary>
    /// A shell line, after <see cref="StopStageFunction"/>, that stops every stage in the workspace that
    /// <paramref name="workspace"/> (a shell word, such as <c>"$workspace"</c>) names.
    /// </summary>
    internal static string StopEveryStageIn(string workspace) =>
        $"for file in {workspace}/*/{PidFile}; do stop_stage \"$file\" {workspace}; done";

    /// <summary>
    /// Ends a stalled stage's process group and returns true, or returns false without stopping anything when the stage wrote its
    /// exit code since the poll that found it stalled, so a stage that has just finished is never failed. Safe to resend.
    /// </summary>
    private async Task<bool> StopStalledStageAsync(string stageDirectory, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var command = $"{StopStageFunction}; s={Quote(stageDirectory)}; if test -s \"$s/{ExitCodeFile}\"; then printf 'ended'; else stop_stage \"$s/{PidFile}\" {Quote(workspace)}; printf 'stopped'; fi";
        var result = await shell.SshAsync(command, progress: progress, cancellationToken: cancellationToken);
        EnsureSuccess(result, "Stopping the stalled stage");
        return result.StandardOutput.Trim() != "ended";
    }

    private string RetainedLogPath(string stageDirectory, string sourcePath) =>
        Path.Combine(Path.GetDirectoryName(sourcePath)!, TemporaryOutputClassifier.FolderName, $"{jobId:N}.{Path.GetFileName(stageDirectory)}.remote.log");

    /// <summary>Keeps the stage's whole log beside the original and returns it with its path.</summary>
    private async Task<(string Path, string Text)> RetainLogAsync(string stageDirectory, string sourcePath, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var destination = RetainedLogPath(stageDirectory, sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var log = Quote(InStage(stageDirectory, LogFile));
        var result = await shell.SshAsync($"test ! -f {log} || cat {log}", progress: progress, cancellationToken: cancellationToken);
        await File.WriteAllTextAsync(destination, result.StandardOutput, cancellationToken);
        return (destination, result.StandardOutput);
    }
}
