using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Infrastructure.Processing;

public sealed partial class RemoteSshProcessingSession : IProcessingSession
{
    /// <summary>A step that has run this long prints its first progress line, and then at most one per interval.</summary>
    internal static readonly TimeSpan ProgressDelay = TimeSpan.FromSeconds(10);

    private readonly JobRecord _job;
    private readonly AppSettings _settings;
    private readonly IExternalProcessRunner _processes;
    private readonly Func<CancellationToken, Task> _pollDelay;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<CancellationToken, Task> _transferPollDelay;
    private readonly string _root;
    private string? _activeStage;

    public RemoteSshProcessingSession(JobRecord job, AppSettings settings, IExternalProcessRunner processes)
        : this(job, settings, processes, cancellationToken => Task.Delay(TimeSpan.FromSeconds(10), cancellationToken))
    {
    }

    /// <param name="pollDelay">The wait between polls of a running remote stage.</param>
    /// <param name="clock">Elapsed time, for timing transfers and deciding when a progress line is due; tests advance it by hand.</param>
    /// <param name="transferPollDelay">The wait between checks of a running transfer; each due check of an upload is an SSH connection.</param>
    internal RemoteSshProcessingSession(JobRecord job, AppSettings settings, IExternalProcessRunner processes, Func<CancellationToken, Task> pollDelay, Func<TimeSpan>? clock = null, Func<CancellationToken, Task>? transferPollDelay = null)
    {
        _job = job;
        _settings = settings;
        _processes = processes;
        _pollDelay = pollDelay;
        _clock = clock ?? StartClock();
        _transferPollDelay = transferPollDelay ?? (cancellationToken => Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
        _root = settings.Processing.RemoteSsh.WorkingDirectory.TrimEnd('/');
        RemoteWorkspace = $"{_root}/{job.Id:N}";
        if (!GuidWorkspacePattern().IsMatch(RemoteWorkspace)) throw new InvalidOperationException("The generated remote workspace is not a safe GUID workspace.");
    }

    private static Func<TimeSpan> StartClock()
    {
        var stopwatch = Stopwatch.StartNew();
        return () => stopwatch.Elapsed;
    }

    public bool IsRemote => true;
    public string ExecutionMode => "remoteSsh";
    public string? RemoteHost => _settings.Processing.RemoteSsh.Host;
    public string? RemoteWorkspace { get; }

    internal string RemoteSourcePath => $"{RemoteWorkspace}/source{Path.GetExtension(_job.SourcePath).ToLowerInvariant()}";
    internal string CrfDirectory => $"{RemoteWorkspace}/crf";
    internal string EncodeDirectory(int attempt) => $"{RemoteWorkspace}/encode-{attempt.ToString(CultureInfo.InvariantCulture)}";

    public Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default) =>
        StageAsync(sourcePath, sourceFingerprint, null, cancellationToken);

    public async Task StageAsync(string sourcePath, string sourceFingerprint, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default)
    {
        // Every check below is silent: a phone video's hash and the SSH round trips take a second or two, and a line per
        // check buried the lines that matter. Only the transfer, which can take minutes, announces itself and reports.
        var source = new FileInfo(sourcePath);
        var requiredDisk = CalculateRequiredDisk(source.Length, _settings.Processing.RemoteSsh.MinimumFreeDiskMultiplier);
        var minimumMemory = HumanReadableValues.TryParseSize(_settings.Processing.RemoteSsh.MinimumAvailableMemory, out var parsedMemory) ? parsedMemory : 0;
        var preflight = await SshAsync($"mkdir -p -- {Quote(RemoteWorkspace!)} && printf '%s %s %s' \"$(nproc)\" \"$(awk '/MemAvailable:/ {{printf \"%.0f\", $2 * 1024}}' /proc/meminfo)\" \"$(df -B1 --output=avail {Quote(RemoteWorkspace!)} | tail -n 1)\"", cancellationToken: cancellationToken);
        var values = preflight.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length != 3 || !int.TryParse(values[0], CultureInfo.InvariantCulture, out var cpus) || !long.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var memory) || !long.TryParse(values[2], CultureInfo.InvariantCulture, out var disk))
            throw new InvalidDataException("Remote resource preflight returned an unexpected response.");
        if (cpus < _settings.Processing.RemoteSsh.MinimumCpuCount) throw new InvalidOperationException($"Remote host has {cpus} CPUs; {_settings.Processing.RemoteSsh.MinimumCpuCount} are required.");
        if (memory < minimumMemory) throw new InvalidOperationException($"Remote host has {memory} available bytes; {minimumMemory} are required.");

        var localHash = await HashFileAsync(sourcePath, progress, cancellationToken);
        var existing = await SshAsync($"if test -f {Quote(RemoteSourcePath)}; then stat -c '%s' {Quote(RemoteSourcePath)}; sha256sum {Quote(RemoteSourcePath)} | cut -d' ' -f1; fi", cancellationToken: cancellationToken);
        var existingParts = existing.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (existingParts.Length == 2 && existingParts[0] == source.Length.ToString(CultureInfo.InvariantCulture) && string.Equals(existingParts[1], localHash, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(JobProgress.Step("Already on the worker, checksum verified."));
            return;
        }
        if (disk < requiredDisk) throw new InvalidOperationException($"Remote host has {disk} free bytes; {requiredDisk} are required for this original.");

        var partial = RemoteSourcePath + ".partial";
        var verifiedPartialLength = 0L;
        var partialState = await SshAsync($"if test -f {Quote(partial)}; then stat -c '%s' {Quote(partial)}; sha256sum {Quote(partial)} | cut -d' ' -f1; fi", cancellationToken: cancellationToken);
        var partialParts = partialState.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (partialParts.Length == 2 && long.TryParse(partialParts[0], CultureInfo.InvariantCulture, out var partialLength) && partialLength > 0 && partialLength <= source.Length)
        {
            var localPrefixHash = await HashFilePrefixAsync(sourcePath, partialLength, progress, cancellationToken);
            if (string.Equals(localPrefixHash, partialParts[1], StringComparison.OrdinalIgnoreCase))
                verifiedPartialLength = partialLength;
            else
                _ = await SshAsync($"rm -f -- {Quote(partial)}", cancellationToken: cancellationToken);
        }
        else if (partialParts.Length != 0)
        {
            _ = await SshAsync($"rm -f -- {Quote(partial)}", cancellationToken: cancellationToken);
        }

        var size = HumanReadableValues.FormatSize(source.Length);
        var started = _clock();
        if (verifiedPartialLength < source.Length)
        {
            progress?.Report(JobProgress.Step(verifiedPartialLength > 0
                ? $"Resuming the upload at {HumanReadableValues.FormatSize(verifiedPartialLength)} of {size}..."
                : $"Uploading {size} to the worker..."));
            var transferCommand = verifiedPartialLength > 0 ? "reput" : "put";
            await SftpAsync($"{transferCommand} {SftpQuote(sourcePath)} {SftpQuote(partial)}\n", cancellationToken,
                new TransferMonitor(new ThrottledProgress(progress, _clock, source.Length, verifiedPartialLength, showRate: true), verifiedPartialLength, async token => await RemoteFileSizeAsync(partial, token)));
        }
        var verify = await SshAsync($"test \"$(stat -c '%s' {Quote(partial)})\" = {source.Length.ToString(CultureInfo.InvariantCulture)} && test \"$(sha256sum {Quote(partial)} | cut -d' ' -f1)\" = {Quote(localHash.ToLowerInvariant())} && mv -f -- {Quote(partial)} {Quote(RemoteSourcePath)}", cancellationToken: cancellationToken);
        EnsureSuccess(verify, "Remote original verification");
        // A complete partial left by an earlier run needed no transfer now, so there is no duration to report.
        progress?.Report(JobProgress.Step(verifiedPartialLength < source.Length
            ? $"Uploaded {size} in {HumanReadableValues.FormatDuration(_clock() - started)}, checksum verified."
            : "Already on the worker, checksum verified."));
    }

    public async Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var arguments = new AbAv1CrfSearchClient("ab-av1").BuildArguments(RemoteSourcePath, settings);
        var elapsed = Stopwatch.StartNew();
        var log = await RunStageAsync(CrfDirectory, arguments, sourcePath, progress, cancellationToken, failedLog => AbAv1Output.NoWorthwhileCrf(failedLog, settings));
        elapsed.Stop();
        var crf = CrfSearchOutputParser.Parse(log);
        return new CrfSearchResult(crf, log, string.Empty, elapsed.Elapsed, CrfSearchOutputParser.TryParsePrediction(log, crf, out var prediction) ? prediction : null);
    }

    public async Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var directory = EncodeDirectory(attempt);
        var remoteOutput = $"{directory}/output{Path.GetExtension(outputPath).ToLowerInvariant()}";
        var arguments = new AbAv1VideoEncoder("ab-av1").BuildArguments(RemoteSourcePath, remoteOutput, crf, settings, flacAudioStreams);
        var elapsed = Stopwatch.StartNew();
        _ = await RunStageAsync(directory, arguments, sourcePath, progress, cancellationToken);
        elapsed.Stop();
        var check = await SshAsync($"test -s {Quote(remoteOutput)}", cancellationToken: cancellationToken);
        EnsureSuccess(check, "Remote encoded output verification");
        return new ProcessingStageResult(outputPath, elapsed.Elapsed);
    }

    public Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) =>
        RetrieveOutputAsync(outputPath, attempt, null, cancellationToken);

    public async Task RetrieveOutputAsync(string outputPath, int attempt, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default)
    {
        var remoteOutput = $"{EncodeDirectory(attempt)}/output{Path.GetExtension(outputPath).ToLowerInvariant()}";
        var hashResult = await SshAsync($"stat -c '%s' {Quote(remoteOutput)}; sha256sum {Quote(remoteOutput)} | cut -d' ' -f1", cancellationToken: cancellationToken);
        EnsureSuccess(hashResult, "Remote output checksum");
        var hashParts = hashResult.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hashParts.Length == 0) throw new InvalidDataException("Remote output checksum was missing.");
        var outputLength = hashParts.Length > 1 && long.TryParse(hashParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOutputLength) ? parsedOutputLength : 0;
        var expectedHash = hashParts[^1];
        if (!HashPattern().IsMatch(expectedHash)) throw new InvalidDataException("Remote output checksum was invalid.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var partial = outputPath + ".partial";
        if (File.Exists(outputPath) && string.Equals(await HashFileAsync(outputPath, progress, cancellationToken), expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(JobProgress.Step("Already downloaded, checksum verified."));
            return;
        }
        var partialStart = File.Exists(partial) && outputLength > 0
            ? Math.Min(new FileInfo(partial).Length, outputLength)
            : 0;
        var size = HumanReadableValues.FormatSize(outputLength);
        progress?.Report(JobProgress.Step(partialStart > 0
            ? $"Resuming the download at {HumanReadableValues.FormatSize(partialStart)} of {size}..."
            : $"Downloading {size} from the worker..."));
        var started = _clock();
        await DownloadAsync(remoteOutput, partial, outputLength, partialStart, progress, cancellationToken);
        var actualHash = await HashFileAsync(partial, progress, cancellationToken);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(JobProgress.Detail("Checksum mismatch; downloading again."));
            File.Delete(partial);
            await DownloadAsync(remoteOutput, partial, outputLength, 0, progress, cancellationToken);
            actualHash = await HashFileAsync(partial, progress, cancellationToken);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException("Downloaded output checksum does not match the remote output after a clean retry.");
            }
        }
        File.Move(partial, outputPath, overwrite: true);
        progress?.Report(JobProgress.Step($"Downloaded {size} in {HumanReadableValues.FormatDuration(_clock() - started)}, checksum verified."));
    }

    private Task DownloadAsync(string remoteOutput, string partial, long outputLength, long partialStart, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        SftpAsync($"reget {SftpQuote(remoteOutput)} {SftpQuote(partial)}\n", cancellationToken,
            new TransferMonitor(new ThrottledProgress(progress, _clock, outputLength, partialStart, showRate: true), partialStart, _ => Task.FromResult(File.Exists(partial) ? new FileInfo(partial).Length : 0)));

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (_activeStage is null) return;
        var pidFile = $"{_activeStage}/pid";
        var command = $"i=0; while test ! -s {Quote(pidFile)} && test \"$i\" -lt 3; do sleep 1; i=$((i+1)); done; if test -s {Quote(pidFile)}; then p=$(cat {Quote(pidFile)}); kill -TERM -- -\"$p\" 2>/dev/null || true; i=0; while kill -0 \"$p\" 2>/dev/null && test \"$i\" -lt 10; do sleep 1; i=$((i+1)); done; kill -KILL -- -\"$p\" 2>/dev/null || true; fi";
        _ = await SshAsync(command, retry: false, cancellationToken: cancellationToken);
    }

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        var expected = $"{_root}/{_job.Id:N}";
        if (!string.Equals(RemoteWorkspace, expected, StringComparison.Ordinal) || !GuidWorkspacePattern().IsMatch(expected)) throw new InvalidOperationException("Refusing unsafe remote workspace cleanup.");
        var result = await SshAsync($"rm -rf -- {Quote(expected)}", cancellationToken: cancellationToken);
        EnsureSuccess(result, "Remote cleanup");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <param name="interpretFailure">Turns the log of a stage that exited non-zero into a more specific exception than the generic one,
    /// or returns null to keep the generic one.</param>
    private async Task<string> RunStageAsync(string stageDirectory, IReadOnlyList<string> arguments, string sourcePath, IProgress<JobProgress>? progress, CancellationToken cancellationToken, Func<string, Exception?>? interpretFailure = null)
    {
        _activeStage = stageDirectory;
        var state = await GetStageStateAsync(stageDirectory, cancellationToken);
        var useCurrentState = true;
        if (state.ExitCode is null && !state.Running)
        {
            var scriptPath = $"{stageDirectory}/run-{Guid.NewGuid():N}.sh";
            var cacheDirectory = $"{stageDirectory}/cache";
            var command = string.Join(" ", new[] { Quote("/usr/bin/time"), Quote("-v"), Quote("ab-av1") }.Concat(arguments.Select(Quote)));
            var script = $"#!/usr/bin/env bash\nset +e\numask 077\nmkdir -p -- {Quote(cacheDirectory)}\nexport XDG_CACHE_HOME={Quote(cacheDirectory)}\nprintf '%s\\n' \"$$\" > pid.partial\nmv -f pid.partial pid\n{command} > log 2>&1\ncode=$?\nprintf '%s\\n' \"$code\" > exit-code.partial\nmv -f exit-code.partial exit-code\nexit 0\n";
            var create = await SshAsync($"mkdir -p -- {Quote(stageDirectory)} && umask 077 && cat > {Quote(scriptPath)} && chmod 700 {Quote(scriptPath)}", script, cancellationToken: cancellationToken);
            EnsureSuccess(create, "Remote stage script upload");
            var launch = await SshAsync(BuildLaunchCommand(stageDirectory, scriptPath), cancellationToken: cancellationToken);
            EnsureSuccess(launch, "Remote stage launch");
            useCurrentState = false;
        }

        var reportedLength = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!useCurrentState) state = await GetStageStateAsync(stageDirectory, cancellationToken);
            useCurrentState = false;
            if (state.Log.Length > reportedLength)
            {
                var added = state.Log[reportedLength..];
                foreach (var line in added.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) progress?.Report(JobProgress.Tool(line));
                reportedLength = state.Log.Length;
            }
            if (state.ExitCode is { } exitCode)
            {
                var logPath = await RetainLogAsync(stageDirectory, sourcePath, cancellationToken);
                _activeStage = null;
                if (exitCode != 0)
                {
                    await CleanupAsync(CancellationToken.None);
                    if (interpretFailure?.Invoke(state.Log) is { } specific) throw specific;
                    var reason = AbAv1Output.LastErrorLine(state.Log);
                    throw new AbAv1StageException($"Remote processing stage failed with exit code {exitCode}{(reason is null ? "." : $": {reason}")}", logPath);
                }
                return state.Log;
            }
            if (!state.Running) throw new InvalidOperationException("Remote stage has neither a running process nor a completion marker.");
            await _pollDelay(cancellationToken);
        }
    }

    /// <summary>
    /// The braces put only the stage script in the background. Without them, <c>&amp;</c> backgrounds the whole
    /// <c>cd … &amp;&amp; nohup …</c> list as a subshell that inherits the SSH channel's output, so the launch's ssh stays
    /// open until ab-av1 exits and the poll loop then prints the whole stage log at once.
    /// </summary>
    internal static string BuildLaunchCommand(string stageDirectory, string scriptPath) =>
        $"cd -- {Quote(stageDirectory)} && {{ nohup setsid bash {Quote(scriptPath)} </dev/null >/dev/null 2>&1 & }}";

    private async Task<(bool Running, int? ExitCode, string Log)> GetStageStateAsync(string stageDirectory, CancellationToken cancellationToken)
    {
        var command = $"if test -s {Quote(stageDirectory + "/exit-code")}; then printf 'completed '; cat {Quote(stageDirectory + "/exit-code")}; elif test -s {Quote(stageDirectory + "/pid")} && kill -0 \"$(cat {Quote(stageDirectory + "/pid")})\" 2>/dev/null; then printf 'running\\n'; else printf 'missing\\n'; fi; printf '\\036'; test ! -f {Quote(stageDirectory + "/log")} || cat {Quote(stageDirectory + "/log")}";
        var result = await SshAsync(command, cancellationToken: cancellationToken);
        var separator = result.StandardOutput.IndexOf('\x1e');
        if (separator < 0) throw new InvalidDataException("Remote stage state was invalid.");
        var header = result.StandardOutput[..separator].Trim();
        var log = result.StandardOutput[(separator + 1)..];
        if (header.StartsWith("completed ", StringComparison.Ordinal) && int.TryParse(header[10..].Trim(), CultureInfo.InvariantCulture, out var code)) return (false, code, log);
        return (string.Equals(header, "running", StringComparison.Ordinal), null, log);
    }

    private async Task<string> RetainLogAsync(string stageDirectory, string sourcePath, CancellationToken cancellationToken)
    {
        var localDirectory = Path.Combine(Path.GetDirectoryName(sourcePath)!, TemporaryOutputClassifier.FolderName);
        Directory.CreateDirectory(localDirectory);
        var stageName = Path.GetFileName(stageDirectory);
        var destination = Path.Combine(localDirectory, $"{_job.Id:N}.{stageName}.remote.log");
        var result = await SshAsync($"test ! -f {Quote(stageDirectory + "/log")} || cat {Quote(stageDirectory + "/log")}", cancellationToken: cancellationToken);
        await File.WriteAllTextAsync(destination, result.StandardOutput, cancellationToken);
        return destination;
    }

    private async Task<ExternalProcessResult> SshAsync(string command, string? standardInput = null, bool retry = true, CancellationToken cancellationToken = default)
    {
        var attempts = retry ? 3 : 1;
        for (var attempt = 1; ; attempt++)
        {
            ExternalProcessResult result;
            try
            {
                var arguments = OpenSshConnectionArguments.Build(_settings.Processing.RemoteSsh);
                arguments.Add(RemoteHost!);
                arguments.Add("bash -lc " + Quote(command));
                result = await _processes.RunAsync(new ExternalProcessRequest(_settings.Tools.SshPath, arguments, standardInput), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && attempt >= attempts)
            {
                throw new RemoteConnectionException("Could not connect to the remote processing host.", exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }
            if (result.ExitCode != 255) return result;
            if (attempt >= attempts) throw new RemoteConnectionException($"SSH connection failed: {FirstLine(result.StandardError)}");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task SftpAsync(string batch, CancellationToken cancellationToken, TransferMonitor? monitor = null)
    {
        if (monitor is null)
        {
            await SftpCoreAsync(batch, cancellationToken);
            return;
        }

        var transfer = SftpCoreAsync(batch, cancellationToken);
        try
        {
            while (!transfer.IsCompleted)
            {
                await _transferPollDelay(cancellationToken);
                if (transfer.IsCompleted) break;
                await monitor.ReportAsync(cancellationToken);
            }

            await transfer;
        }
        catch
        {
            if (cancellationToken.IsCancellationRequested)
            {
                try { await transfer; } catch { }
            }
            throw;
        }
    }

    private async Task SftpCoreAsync(string batch, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ExternalProcessResult result;
            try
            {
                var arguments = OpenSshConnectionArguments.Build(_settings.Processing.RemoteSsh);
                arguments.Add("-b");
                arguments.Add("-");
                arguments.Add(RemoteHost!);
                result = await _processes.RunAsync(new ExternalProcessRequest(_settings.Tools.SftpPath, arguments, batch), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && attempt == 3)
            {
                throw new RemoteConnectionException("Could not connect to the remote processing host for file transfer.", exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }
            if (result.ExitCode == 0) return;
            if (result.ExitCode != 255) throw new InvalidOperationException($"SFTP transfer failed: {FirstLine(result.StandardError)}");
            if (attempt == 3) throw new RemoteConnectionException($"SFTP connection failed: {FirstLine(result.StandardError)}");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    internal static string SftpQuote(string value) => "\"" + value.Replace('\\', '/').Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    internal static long CalculateRequiredDisk(long sourceBytes, double multiplier) => checked((long)Math.Ceiling(sourceBytes * multiplier));

    private static void EnsureSuccess(ExternalProcessResult result, string operation)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException($"{operation} failed: {FirstLine(result.StandardError)}");
    }

    private static string FirstLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "No error output.";

    private async Task<long> RemoteFileSizeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await SshAsync($"if test -f {Quote(path)}; then stat -c '%s' {Quote(path)}; else printf '0'; fi", cancellationToken: cancellationToken);
        EnsureSuccess(result, "Remote file size check");
        return long.TryParse(result.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) && length >= 0 ? length : 0;
    }

    private async Task<string> HashFileAsync(string path, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return await HashAsync(stream, stream.Length, progress, cancellationToken);
    }

    private async Task<string> HashFilePrefixAsync(string path, long length, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return await HashAsync(stream, length, progress, cancellationToken);
    }

    /// <summary>
    /// Hashes the first <paramref name="length"/> bytes of <paramref name="stream"/>. Most hashes of a phone video take a
    /// second or two and print nothing; only one that outlasts <see cref="ProgressDelay"/> reports, so a multi-gigabyte
    /// original does not look hung.
    /// </summary>
    private async Task<string> HashAsync(FileStream stream, long length, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        var processed = 0L;
        var reporter = new ThrottledProgress(progress, _clock, length, 0, showRate: false);
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) throw new EndOfStreamException("The file became shorter while its checksum was being calculated.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
            processed += read;
            reporter.Report(processed);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// Polls how far a transfer has got, but only when a progress line is due, so a transfer that finishes within
    /// <see cref="ProgressDelay"/> costs no extra SSH connections.
    /// </summary>
    private sealed class TransferMonitor(ThrottledProgress reporter, long initialBytes, Func<CancellationToken, Task<long>> currentBytes)
    {
        public async Task ReportAsync(CancellationToken cancellationToken)
        {
            if (!reporter.IsDue) return;
            using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pollTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var bytes = await currentBytes(pollTimeout.Token);
                reporter.Report(Math.Max(initialBytes, bytes));
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                // A monitor-only timeout must not interrupt the transfer itself.
            }
            catch
            {
                // Size polling is advisory. A failed SSH probe must not abort a healthy transfer.
            }
        }
    }

    /// <summary>
    /// A step that runs quickly prints nothing; one that outlasts <see cref="ProgressDelay"/> prints a line, then at most one
    /// every <see cref="ProgressDelay"/>. Transfers show rate and time left, which need some elapsed time and progress to compute.
    /// </summary>
    private sealed class ThrottledProgress(IProgress<JobProgress>? progress, Func<TimeSpan> clock, long totalBytes, long initialBytes, bool showRate)
    {
        private readonly TimeSpan _start = clock();
        private TimeSpan _lastReport = clock();

        public bool IsDue => progress is not null && clock() - _lastReport >= ProgressDelay;

        public void Report(long bytes)
        {
            if (!IsDue) return;
            var now = clock();
            _lastReport = now;
            bytes = Math.Max(0, Math.Min(bytes, totalBytes));
            var percent = totalBytes > 0 ? (int)Math.Min(100, bytes * 100L / totalBytes) : 100;
            if (!showRate)
            {
                progress!.Report(JobProgress.Detail($"Checking the checksum: {percent}% of {HumanReadableValues.FormatSize(totalBytes)}"));
                return;
            }

            var seconds = (now - _start).TotalSeconds;
            var rate = seconds > 0 ? (bytes - initialBytes) / seconds : 0;
            var text = $"{percent}% of {HumanReadableValues.FormatSize(totalBytes)}";
            if (rate > 0)
            {
                text += $", {HumanReadableValues.FormatSize((long)rate)}/s";
                if (bytes < totalBytes) text += $", about {HumanReadableValues.FormatDuration(TimeSpan.FromSeconds((totalBytes - bytes) / rate))} left";
            }
            progress!.Report(JobProgress.Detail(text));
        }
    }

    [GeneratedRegex(@"^/.+?/[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidWorkspacePattern();
    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
