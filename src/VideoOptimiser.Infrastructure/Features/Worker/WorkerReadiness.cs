using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.Infrastructure.Features.Worker;

/// <summary>
/// The worker's readiness job: after a server exists, waits over SSH until its bootstrap script has finished and the
/// videoopt user can connect, reporting the script's progress as it goes.
/// </summary>
internal sealed class WorkerReadiness
{
    internal const string BootstrapProgressPath = "/opt/video-optimiser/.bootstrap-progress";
    internal const string BootstrapCompletePath = "/opt/video-optimiser/.bootstrap-complete";
    internal const string BootstrapFailedPath = "/opt/video-optimiser/.bootstrap-failed";
    private const string CloudInitOutputLogPath = "/var/log/cloud-init-output.log";
    // The marker and the log come from the worker: cap what of them reaches the error.
    private const int FailureLogLines = 5;
    private const int FailureValueMaxLength = 300;
    private static readonly TimeSpan DefaultReadinessProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProcessTerminationGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultProgressHeartbeatInterval = TimeSpan.FromSeconds(60);
    private readonly IExternalProcessRunner _processes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _readinessProbeTimeout;
    private readonly TimeSpan _progressHeartbeatInterval;
    private readonly Func<DateTimeOffset> _utcNow;

    public WorkerReadiness(
        IExternalProcessRunner processes,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan? readinessProbeTimeout,
        TimeSpan? progressHeartbeatInterval,
        Func<DateTimeOffset>? utcNow)
    {
        _processes = processes;
        _delay = delay;
        _readinessProbeTimeout = readinessProbeTimeout ?? DefaultReadinessProbeTimeout;
        if (_readinessProbeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(readinessProbeTimeout));
        _progressHeartbeatInterval = progressHeartbeatInterval ?? DefaultProgressHeartbeatInterval;
        if (_progressHeartbeatInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(progressHeartbeatInterval));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// SSH is refused or times out for the first minutes of every new server while it boots, so a failure is expected and
    /// stays silent until it has lasted this long.
    /// </summary>
    private static readonly TimeSpan SshFailureGracePeriod = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan SshFailureReportInterval = TimeSpan.FromMinutes(1);

    public async Task WaitUntilReadyAsync(AppSettings settings, WorkerState state, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!HumanReadableValues.TryParseDuration(settings.Processing.RemoteSsh.Hetzner.BootstrapTimeout, out var timeout))
            throw new InvalidOperationException("The configured Hetzner bootstrap timeout is invalid.");
        var waitStartedAt = _utcNow();
        var deadline = waitStartedAt + timeout;
        (int Percent, string Stage)? lastProgress = null;
        DateTimeOffset? phaseStartedAt = null;
        DateTimeOffset? lastHeartbeatAt = null;
        // The current run of failed checks: when it began, and when it was last reported.
        DateTimeOffset? failingSince = null;
        DateTimeOffset? lastFailureReportAt = null;

        void Failed(string reason)
        {
            var now = _utcNow();
            failingSince ??= now;
            if (now - failingSince < SshFailureGracePeriod) return;
            if (lastFailureReportAt is not null && now - lastFailureReportAt < SshFailureReportInterval) return;
            lastFailureReportAt = now;
            progress?.Report($"Still waiting for the worker's SSH ({HumanReadableValues.FormatDuration(now - failingSince.Value)}): {reason}");
        }

        // A failed script leaves its progress file at the last stage, so the failure marker is checked before it (VOPT-140).
        // Complete comes first: a failure after the script marked itself complete still leaves a usable worker.
        var probeCommand = $"if test -f {BootstrapCompletePath}; then printf 'percent=100\\nstage=Ready\\n'; " +
            $"elif test -f {BootstrapFailedPath}; then printf 'failed=1\\n'; cat {BootstrapFailedPath}; tail -n {FailureLogLines} {CloudInitOutputLogPath} 2>/dev/null | sed 's/^/log=/'; " +
            $"elif test -f {BootstrapProgressPath}; then cat {BootstrapProgressPath}; else printf 'percent=0\\nstage=Starting\\n'; fi";
        while (_utcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A worker this version creates has its host key recorded before it boots (VOPT-152), so every probe checks
            // strictly, and a key mismatch is just a failed probe, retried like a refused one. Only a worker with no recorded
            // key trusts its first answer, as every worker did before: one an earlier version created that never answered
            // a probe, or one recovered by its label after its local files were lost. Read again each time, because the
            // first accepted probe records the key.
            var acceptNewHostKey = !WorkerStateStore.HasHostKey(settings.Processing.RemoteSsh.KnownHostsFile);
            var arguments = OpenSshConnectionArguments.Build(settings.Processing.RemoteSsh, acceptNewHostKey, userOverride: "root");
            arguments.Add(state.Address);
            arguments.Add(probeCommand);
            try
            {
                var result = await RunReadinessProbeAsync(settings.Tools.SshPath, arguments, cancellationToken);
                if (result is null)
                {
                    Failed($"status check exceeded {_readinessProbeTimeout.TotalSeconds:0}s");
                }
                else if (result.ExitCode == 0 && TryParseBootstrapFailure(result.StandardOutput, state.Name) is { } failure)
                {
                    throw new RemoteWorkerBootstrapFailedException(failure);
                }
                else if (result.ExitCode == 0 && TryParseBootstrapProgress(result.StandardOutput, out var currentProgress))
                {
                    failingSince = null;
                    if (currentProgress.Percent >= 100)
                    {
                        var processingArguments = OpenSshConnectionArguments.Build(settings.Processing.RemoteSsh, acceptNewHostKey: !WorkerStateStore.HasHostKey(settings.Processing.RemoteSsh.KnownHostsFile));
                        processingArguments.Add(state.Address);
                        processingArguments.Add("true");
                        var processingResult = await RunReadinessProbeAsync(settings.Tools.SshPath, processingArguments, cancellationToken);
                        if (processingResult?.ExitCode == 0)
                        {
                            progress?.Report($"Worker ready after {HumanReadableValues.FormatDuration(_utcNow() - waitStartedAt)}.");
                            return;
                        }

                        Failed(processingResult is null
                            ? $"videoopt SSH check exceeded {_readinessProbeTimeout.TotalSeconds:0}s"
                            : FirstNonEmpty(processingResult.StandardError, processingResult.StandardOutput, $"videoopt SSH exited with code {processingResult.ExitCode}"));
                        // The script is done but the worker is not usable yet: say nothing of 100% (it reads as ready), and check again.
                        await _delay(TimeSpan.FromSeconds(15), cancellationToken);
                        continue;
                    }

                    var observedAt = _utcNow();
                    if (lastProgress != currentProgress)
                    {
                        progress?.Report($"Bootstrapping: {currentProgress.Percent}%, {DescribeStage(currentProgress.Stage)}.");
                        lastProgress = currentProgress;
                        phaseStartedAt = observedAt;
                        lastHeartbeatAt = observedAt;
                    }
                    else if (phaseStartedAt is not null && lastHeartbeatAt is not null && observedAt - lastHeartbeatAt >= _progressHeartbeatInterval)
                    {
                        progress?.Report($"Bootstrapping: {currentProgress.Percent}%, {DescribeStage(currentProgress.Stage)} ({HumanReadableValues.FormatDuration(observedAt - phaseStartedAt.Value)} in this step).");
                        lastHeartbeatAt = observedAt;
                    }
                }
                else if (result is not null)
                {
                    Failed(FirstNonEmpty(result.StandardError, result.StandardOutput, $"exited with code {result.ExitCode}"));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not RemoteWorkerBootstrapFailedException)
            {
                Failed(exception.Message);
            }
            await _delay(TimeSpan.FromSeconds(15), cancellationToken);
        }
        throw new RemoteWorkerBootstrapFailedException($"The Hetzner worker {state.Name} did not finish bootstrapping within {settings.Processing.RemoteSsh.Hetzner.BootstrapTimeout}.");
    }

    // The script's stage names ("Building FFmpeg") start a sentence; here they continue one.
    private static string DescribeStage(string stage) => stage.Length == 0 ? stage : char.ToLowerInvariant(stage[0]) + stage[1..];

    private async Task<ExternalProcessResult?> RunReadinessProbeAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runTask = _processes.RunAsync(new ExternalProcessRequest(executable, arguments), probeCancellation.Token);
        try
        {
            return await runTask.WaitAsync(_readinessProbeTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            probeCancellation.Cancel();
            await AwaitProcessTerminationAsync(runTask);
            probeCancellation.Dispose();
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            probeCancellation.Cancel();
            await AwaitProcessTerminationAsync(runTask);
            probeCancellation.Dispose();
            return null;
        }
        catch (OperationCanceledException)
        {
            probeCancellation.Cancel();
            _ = ObserveProcessAsync(runTask, probeCancellation);
            throw;
        }
        finally
        {
            if (runTask.IsCompleted) probeCancellation.Dispose();
        }
    }

    private static async Task AwaitProcessTerminationAsync(Task<ExternalProcessResult> processTask)
    {
        try
        {
            await processTask.WaitAsync(ProcessTerminationGracePeriod).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
        }
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "unknown SSH failure";

    private static async Task ObserveProcessAsync(Task<ExternalProcessResult> processTask, CancellationTokenSource cancellation)
    {
        try
        {
            await processTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// The error for a probe answer that carries the script's failure marker (<c>failed=1</c>, then the marker's
    /// <c>stage</c> and either <c>error</c> from <c>die</c> or <c>status</c>/<c>line</c>/<c>command</c> from the ERR trap, then
    /// <c>log=</c> lines from the cloud-init log), or null for any other answer.
    /// </summary>
    private static string? TryParseBootstrapFailure(string output, string workerName)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var log = new List<string>();
        var failed = false;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator];
            var value = Cap(line[(separator + 1)..].Trim());
            if (key.Equals("failed", StringComparison.OrdinalIgnoreCase)) failed = true;
            else if (key.Equals("log", StringComparison.OrdinalIgnoreCase))
            {
                // die's own line is already the error; a log line repeating it says nothing more.
                if (value.Length > 0 && !value.StartsWith("bootstrap error: ", StringComparison.Ordinal)) log.Add(value);
            }
            else values.TryAdd(key, value);
        }
        if (!failed) return null;

        var stage = values.GetValueOrDefault("stage") is { Length: > 0 } named ? $" while {DescribeStage(named)}" : string.Empty;
        string cause;
        if (values.GetValueOrDefault("error") is { Length: > 0 } error) cause = error;
        else if (values.GetValueOrDefault("command") is { Length: > 0 } command)
        {
            var status = values.GetValueOrDefault("status") is { Length: > 0 } code ? $" with code {code}" : string.Empty;
            var where = values.GetValueOrDefault("line") is { Length: > 0 } number ? $" (line {number} of the bootstrap script)" : string.Empty;
            cause = $"{command} exited{status}{where}";
        }
        else cause = "the bootstrap script stopped with an error";

        var message = $"The Hetzner worker {workerName} failed to bootstrap{stage}: {cause}.";
        return log.Count == 0 ? message : $"{message}{Environment.NewLine}The end of its cloud-init log:{Environment.NewLine}{string.Join(Environment.NewLine, log.Select(line => "  " + line))}";
    }

    private static string Cap(string value) => value.Length <= FailureValueMaxLength ? value : value[..(FailureValueMaxLength - 1)] + "…";

    private static bool TryParseBootstrapProgress(string output, out (int Percent, string Stage) progress)
    {
        var percent = -1;
        var stage = string.Empty;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator];
            var value = line[(separator + 1)..];
            if (key.Equals("percent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, CultureInfo.InvariantCulture, out var parsedPercent)) percent = parsedPercent;
            else if (key.Equals("stage", StringComparison.OrdinalIgnoreCase)) stage = value;
        }

        if (percent is < 0 or > 100 || string.IsNullOrWhiteSpace(stage))
        {
            progress = default;
            return false;
        }

        progress = (percent, stage);
        return true;
    }
}
