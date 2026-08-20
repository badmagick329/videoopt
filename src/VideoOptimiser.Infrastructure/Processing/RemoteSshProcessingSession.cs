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
    private readonly JobRecord _job;
    private readonly AppSettings _settings;
    private readonly IExternalProcessRunner _processes;
    private readonly Func<CancellationToken, Task> _pollDelay;
    private readonly string _root;
    private string? _activeStage;

    public RemoteSshProcessingSession(JobRecord job, AppSettings settings, IExternalProcessRunner processes)
        : this(job, settings, processes, cancellationToken => Task.Delay(TimeSpan.FromSeconds(10), cancellationToken))
    {
    }

    internal RemoteSshProcessingSession(JobRecord job, AppSettings settings, IExternalProcessRunner processes, Func<CancellationToken, Task> pollDelay)
    {
        _job = job;
        _settings = settings;
        _processes = processes;
        _pollDelay = pollDelay;
        _root = settings.Processing.RemoteSsh.WorkingDirectory.TrimEnd('/');
        RemoteWorkspace = $"{_root}/{job.Id:N}";
        if (!GuidWorkspacePattern().IsMatch(RemoteWorkspace)) throw new InvalidOperationException("The generated remote workspace is not a safe GUID workspace.");
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

    public async Task StageAsync(string sourcePath, string sourceFingerprint, IProgress<CrfSearchOutput>? progress, CancellationToken cancellationToken = default)
    {
        var source = new FileInfo(sourcePath);
        var requiredDisk = CalculateRequiredDisk(source.Length, _settings.Processing.RemoteSsh.MinimumFreeDiskMultiplier);
        var minimumMemory = HumanReadableValues.TryParseSize(_settings.Processing.RemoteSsh.MinimumAvailableMemory, out var parsedMemory) ? parsedMemory : 0;
        progress?.Report(new CrfSearchOutput("staging", "Staging: checking remote CPU, memory, and disk capacity."));
        var preflight = await SshAsync($"mkdir -p -- {Quote(RemoteWorkspace!)} && printf '%s %s %s' \"$(nproc)\" \"$(awk '/MemAvailable:/ {{printf \"%.0f\", $2 * 1024}}' /proc/meminfo)\" \"$(df -B1 --output=avail {Quote(RemoteWorkspace!)} | tail -n 1)\"", cancellationToken: cancellationToken);
        var values = preflight.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length != 3 || !int.TryParse(values[0], CultureInfo.InvariantCulture, out var cpus) || !long.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var memory) || !long.TryParse(values[2], CultureInfo.InvariantCulture, out var disk))
            throw new InvalidDataException("Remote resource preflight returned an unexpected response.");
        if (cpus < _settings.Processing.RemoteSsh.MinimumCpuCount) throw new InvalidOperationException($"Remote host has {cpus} CPUs; {_settings.Processing.RemoteSsh.MinimumCpuCount} are required.");
        if (memory < minimumMemory) throw new InvalidOperationException($"Remote host has {memory} available bytes; {minimumMemory} are required.");

        var localHash = await HashFileAsync(sourcePath, cancellationToken, progress, "Staging: hashing local source");
        progress?.Report(new CrfSearchOutput("staging", "Staging: checking for an existing or resumable remote upload."));
        var existing = await SshAsync($"if test -f {Quote(RemoteSourcePath)}; then stat -c '%s' {Quote(RemoteSourcePath)}; sha256sum {Quote(RemoteSourcePath)} | cut -d' ' -f1; fi", cancellationToken: cancellationToken);
        var existingParts = existing.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (existingParts.Length == 2 && existingParts[0] == source.Length.ToString(CultureInfo.InvariantCulture) && string.Equals(existingParts[1], localHash, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new CrfSearchOutput("staging", "Staging: remote source already matches; reusing it."));
            return;
        }
        if (disk < requiredDisk) throw new InvalidOperationException($"Remote host has {disk} free bytes; {requiredDisk} are required for this source.");

        var partial = RemoteSourcePath + ".partial";
        var verifiedPartialLength = 0L;
        var partialState = await SshAsync($"if test -f {Quote(partial)}; then stat -c '%s' {Quote(partial)}; sha256sum {Quote(partial)} | cut -d' ' -f1; fi", cancellationToken: cancellationToken);
        var partialParts = partialState.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (partialParts.Length == 2 && long.TryParse(partialParts[0], CultureInfo.InvariantCulture, out var partialLength) && partialLength > 0 && partialLength <= source.Length)
        {
            progress?.Report(new CrfSearchOutput("staging", $"Staging: validating resumable upload prefix ({FormatBytes(partialLength)} of {FormatBytes(source.Length)})."));
            var localPrefixHash = await HashFilePrefixAsync(sourcePath, partialLength, cancellationToken, progress, "Staging: validating resumable upload prefix");
            if (string.Equals(localPrefixHash, partialParts[1], StringComparison.OrdinalIgnoreCase))
                verifiedPartialLength = partialLength;
            else
                _ = await SshAsync($"rm -f -- {Quote(partial)}", cancellationToken: cancellationToken);
        }
        else if (partialParts.Length != 0)
        {
            _ = await SshAsync($"rm -f -- {Quote(partial)}", cancellationToken: cancellationToken);
        }
        if (verifiedPartialLength < source.Length)
        {
            var transferCommand = verifiedPartialLength > 0 ? "reput" : "put";
            var transferStart = verifiedPartialLength;
            var transferLabel = transferStart > 0
                ? $"Staging: resuming source upload ({FormatBytes(transferStart)} of {FormatBytes(source.Length)})"
                : $"Staging: uploading source (0% of {FormatBytes(source.Length)})";
            progress?.Report(new CrfSearchOutput("staging", transferLabel));
            await SftpAsync($"{transferCommand} {SftpQuote(sourcePath)} {SftpQuote(partial)}\n", cancellationToken,
                new TransferMonitor(source.Length, transferStart, progress, "Staging: uploading source", async token => await RemoteFileSizeAsync(partial, token)));
            progress?.Report(new CrfSearchOutput("staging", $"Staging: source upload complete (100% of {FormatBytes(source.Length)})."));
        }
        progress?.Report(new CrfSearchOutput("staging", "Staging: verifying the remote source checksum."));
        var verify = await SshAsync($"test \"$(stat -c '%s' {Quote(partial)})\" = {source.Length.ToString(CultureInfo.InvariantCulture)} && test \"$(sha256sum {Quote(partial)} | cut -d' ' -f1)\" = {Quote(localHash.ToLowerInvariant())} && mv -f -- {Quote(partial)} {Quote(RemoteSourcePath)}", cancellationToken: cancellationToken);
        EnsureSuccess(verify, "Remote source verification");
        progress?.Report(new CrfSearchOutput("staging", "Staging: remote source checksum verified."));
    }

    public async Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var arguments = new AbAv1CrfSearchClient("ab-av1").BuildArguments(RemoteSourcePath, settings);
        var elapsed = Stopwatch.StartNew();
        var log = await RunStageAsync(CrfDirectory, arguments, sourcePath, progress, cancellationToken);
        elapsed.Stop();
        return new CrfSearchResult(CrfSearchOutputParser.Parse(log), log, string.Empty, elapsed.Elapsed);
    }

    public async Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var directory = EncodeDirectory(attempt);
        var remoteOutput = $"{directory}/output{Path.GetExtension(outputPath).ToLowerInvariant()}";
        var arguments = new AbAv1VideoEncoder("ab-av1").BuildArguments(RemoteSourcePath, remoteOutput, crf, settings);
        var elapsed = Stopwatch.StartNew();
        _ = await RunStageAsync(directory, arguments, sourcePath, progress, cancellationToken);
        elapsed.Stop();
        var check = await SshAsync($"test -s {Quote(remoteOutput)}", cancellationToken: cancellationToken);
        EnsureSuccess(check, "Remote encoded output verification");
        return new ProcessingStageResult(outputPath, elapsed.Elapsed);
    }

    public Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) =>
        RetrieveOutputAsync(outputPath, attempt, null, cancellationToken);

    public async Task RetrieveOutputAsync(string outputPath, int attempt, IProgress<CrfSearchOutput>? progress, CancellationToken cancellationToken = default)
    {
        var remoteOutput = $"{EncodeDirectory(attempt)}/output{Path.GetExtension(outputPath).ToLowerInvariant()}";
        progress?.Report(new CrfSearchOutput("downloading", "Downloading: obtaining the remote output checksum."));
        var hashResult = await SshAsync($"stat -c '%s' {Quote(remoteOutput)}; sha256sum {Quote(remoteOutput)} | cut -d' ' -f1", cancellationToken: cancellationToken);
        EnsureSuccess(hashResult, "Remote output checksum");
        var hashParts = hashResult.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hashParts.Length == 0) throw new InvalidDataException("Remote output checksum was missing.");
        var outputLength = hashParts.Length > 1 && long.TryParse(hashParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOutputLength) ? parsedOutputLength : 0;
        var expectedHash = hashParts[^1];
        if (!HashPattern().IsMatch(expectedHash)) throw new InvalidDataException("Remote output checksum was invalid.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var partial = outputPath + ".partial";
        if (File.Exists(outputPath))
        {
            progress?.Report(new CrfSearchOutput("downloading", "Downloading: checking the existing local output."));
            if (string.Equals(await HashFileAsync(outputPath, cancellationToken, progress, "Downloading: checking existing local output"), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new CrfSearchOutput("downloading", "Downloading: local output already matches; reusing it."));
                return;
            }
        }
        var partialStart = File.Exists(partial) && outputLength > 0
            ? Math.Min(new FileInfo(partial).Length, outputLength)
            : 0;
        var transferLabel = partialStart > 0
            ? $"Downloading: resuming remote output ({FormatBytes(partialStart)} of {FormatBytes(outputLength)})"
            : $"Downloading: receiving remote output (0% of {FormatBytes(outputLength)})";
        progress?.Report(new CrfSearchOutput("downloading", transferLabel));
        await SftpAsync($"reget {SftpQuote(remoteOutput)} {SftpQuote(partial)}\n", cancellationToken,
            new TransferMonitor(outputLength, partialStart, progress, "Downloading: receiving remote output", async _ => File.Exists(partial) ? new FileInfo(partial).Length : 0));
        var actualHash = await HashFileAsync(partial, cancellationToken, progress, "Downloading: verifying downloaded output");
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new CrfSearchOutput("downloading", "Downloading: checksum mismatch; retrying the transfer cleanly."));
            File.Delete(partial);
            await SftpAsync($"reget {SftpQuote(remoteOutput)} {SftpQuote(partial)}\n", cancellationToken,
                new TransferMonitor(outputLength, 0, progress, "Downloading: retrying remote output", async _ => File.Exists(partial) ? new FileInfo(partial).Length : 0));
            actualHash = await HashFileAsync(partial, cancellationToken, progress, "Downloading: verifying retried output");
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException("Downloaded output checksum does not match the remote output after a clean retry.");
            }
        }
        File.Move(partial, outputPath, overwrite: true);
        progress?.Report(new CrfSearchOutput("downloading", $"Downloading: output verified (100% of {FormatBytes(outputLength)})."));
    }

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

    private async Task<string> RunStageAsync(string stageDirectory, IReadOnlyList<string> arguments, string sourcePath, IProgress<CrfSearchOutput>? progress, CancellationToken cancellationToken)
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
            var launch = await SshAsync($"cd {Quote(stageDirectory)} && nohup setsid bash {Quote(scriptPath)} </dev/null >/dev/null 2>&1 &", cancellationToken: cancellationToken);
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
                foreach (var line in added.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) progress?.Report(new CrfSearchOutput("remote", line));
                reportedLength = state.Log.Length;
            }
            if (state.ExitCode is { } exitCode)
            {
                await RetainLogAsync(stageDirectory, sourcePath, cancellationToken);
                _activeStage = null;
                if (exitCode != 0)
                {
                    await CleanupAsync(CancellationToken.None);
                    throw new InvalidOperationException($"Remote processing stage failed with exit code {exitCode}.");
                }
                return state.Log;
            }
            if (!state.Running) throw new InvalidOperationException("Remote stage has neither a running process nor a completion marker.");
            await _pollDelay(cancellationToken);
        }
    }

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

    private async Task RetainLogAsync(string stageDirectory, string sourcePath, CancellationToken cancellationToken)
    {
        var localDirectory = Path.Combine(Path.GetDirectoryName(sourcePath)!, ".video-optimiser");
        Directory.CreateDirectory(localDirectory);
        var stageName = Path.GetFileName(stageDirectory);
        var destination = Path.Combine(localDirectory, $"{_job.Id:N}.{stageName}.remote.log");
        var result = await SshAsync($"test ! -f {Quote(stageDirectory + "/log")} || cat {Quote(stageDirectory + "/log")}", cancellationToken: cancellationToken);
        await File.WriteAllTextAsync(destination, result.StandardOutput, cancellationToken);
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
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                if (transfer.IsCompleted) break;
                await monitor.ReportAsync(cancellationToken);
            }

            await transfer;
            monitor.ReportCompleted();
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

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken, IProgress<CrfSearchOutput>? progress = null, string? label = null)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var total = stream.Length;
        var processed = 0L;
        var reporter = new ByteProgressReporter(progress, label, total);
        reporter.Report(0);
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            processed += read;
            reporter.Report(processed);
        }
        reporter.ReportCompleted();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> HashFilePrefixAsync(string path, long length, CancellationToken cancellationToken, IProgress<CrfSearchOutput>? progress = null, string? label = null)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        var processed = 0L;
        var reporter = new ByteProgressReporter(progress, label, length);
        reporter.Report(0);
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) throw new EndOfStreamException("Source became shorter while validating the resumable upload.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
            processed += read;
            reporter.Report(processed);
        }
        reporter.ReportCompleted();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string FormatBytes(long bytes)
    {
        const double kilobyte = 1024;
        const double megabyte = kilobyte * 1024;
        const double gigabyte = megabyte * 1024;
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < (long)megabyte => $"{bytes / kilobyte:0.0} KiB",
            < (long)gigabyte => $"{bytes / megabyte:0.0} MiB",
            _ => $"{bytes / gigabyte:0.0} GiB"
        };
    }

    private sealed class TransferMonitor(long totalBytes, long initialBytes, IProgress<CrfSearchOutput>? progress, string label, Func<CancellationToken, Task<long>> currentBytes)
    {
        private readonly ByteProgressReporter _reporter = new(progress, label, totalBytes);
        private readonly Func<CancellationToken, Task<long>> _currentBytes = currentBytes;
        private readonly long _initialBytes = initialBytes;

        public async Task ReportAsync(CancellationToken cancellationToken)
        {
            using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pollTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var bytes = await _currentBytes(pollTimeout.Token);
                _reporter.Report(Math.Max(_initialBytes, bytes));
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

        public void ReportCompleted() => _reporter.ReportCompleted();
    }

    private sealed class ByteProgressReporter(IProgress<CrfSearchOutput>? progress, string? label, long totalBytes)
    {
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly Stopwatch _sinceReport = Stopwatch.StartNew();
        private int _lastPercent = -1;
        private long _lastBytes = -1;

        public void Report(long bytes, bool force = false)
        {
            if (progress is null) return;
            bytes = Math.Max(0, Math.Min(bytes, totalBytes));
            var percent = totalBytes > 0 ? (int)Math.Min(100, bytes * 100L / totalBytes) : 100;
            var heartbeat = _sinceReport.Elapsed >= HeartbeatInterval;
            if (!force && !heartbeat && percent == _lastPercent && bytes != totalBytes) return;
            if (!force && !heartbeat && _lastPercent >= 0 && percent < 100 && percent < _lastPercent + 5) return;
            if (!force && !heartbeat && bytes == _lastBytes) return;
            _lastPercent = percent;
            _lastBytes = bytes;
            progress.Report(new CrfSearchOutput("progress", $"{label ?? "Progress"} ({percent}% of {FormatBytes(totalBytes)}; {FormatBytes(bytes)}; elapsed {FormatDuration(_elapsed.Elapsed)})."));
            _sinceReport.Restart();
        }

        public void ReportCompleted()
        {
            if (progress is null) return;
            Report(totalBytes, force: true);
        }
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m"
        : duration.TotalMinutes >= 1
            ? $"{duration.Minutes}m {duration.Seconds:00}s"
            : $"{duration.Seconds}s";

    [GeneratedRegex(@"^/.+?/[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidWorkspacePattern();
    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
