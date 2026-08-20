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

    public async Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default)
    {
        var source = new FileInfo(sourcePath);
        var requiredDisk = CalculateRequiredDisk(source.Length, _settings.Processing.RemoteSsh.MinimumFreeDiskMultiplier);
        var minimumMemory = HumanReadableValues.TryParseSize(_settings.Processing.RemoteSsh.MinimumAvailableMemory, out var parsedMemory) ? parsedMemory : 0;
        var preflight = await SshAsync($"mkdir -p -- {Quote(RemoteWorkspace!)} && printf '%s %s %s' \"$(nproc)\" \"$(awk '/MemAvailable:/ {{printf \"%.0f\", $2 * 1024}}' /proc/meminfo)\" \"$(df -B1 --output=avail {Quote(RemoteWorkspace!)} | tail -n 1)\"", cancellationToken: cancellationToken);
        var values = preflight.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length != 3 || !int.TryParse(values[0], CultureInfo.InvariantCulture, out var cpus) || !long.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var memory) || !long.TryParse(values[2], CultureInfo.InvariantCulture, out var disk))
            throw new InvalidDataException("Remote resource preflight returned an unexpected response.");
        if (cpus < _settings.Processing.RemoteSsh.MinimumCpuCount) throw new InvalidOperationException($"Remote host has {cpus} CPUs; {_settings.Processing.RemoteSsh.MinimumCpuCount} are required.");
        if (memory < minimumMemory) throw new InvalidOperationException($"Remote host has {memory} available bytes; {minimumMemory} are required.");

        var localHash = await HashFileAsync(sourcePath, cancellationToken);
        var existing = await SshAsync($"if test -f {Quote(RemoteSourcePath)}; then stat -c '%s' {Quote(RemoteSourcePath)}; sha256sum {Quote(RemoteSourcePath)} | cut -d' ' -f1; fi", cancellationToken: cancellationToken);
        var existingParts = existing.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (existingParts.Length == 2 && existingParts[0] == source.Length.ToString(CultureInfo.InvariantCulture) && string.Equals(existingParts[1], localHash, StringComparison.OrdinalIgnoreCase)) return;
        if (disk < requiredDisk) throw new InvalidOperationException($"Remote host has {disk} free bytes; {requiredDisk} are required for this source.");

        var partial = RemoteSourcePath + ".partial";
        var verifiedPartialLength = 0L;
        var partialState = await SshAsync($"if test -f {Quote(partial)}; then stat -c '%s' {Quote(partial)}; sha256sum {Quote(partial)} | cut -d' ' -f1; fi", cancellationToken: cancellationToken);
        var partialParts = partialState.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (partialParts.Length == 2 && long.TryParse(partialParts[0], CultureInfo.InvariantCulture, out var partialLength) && partialLength > 0 && partialLength <= source.Length)
        {
            var localPrefixHash = await HashFilePrefixAsync(sourcePath, partialLength, cancellationToken);
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
            await SftpAsync($"{transferCommand} {SftpQuote(sourcePath)} {SftpQuote(partial)}\n", cancellationToken);
        }
        var verify = await SshAsync($"test \"$(stat -c '%s' {Quote(partial)})\" = {source.Length.ToString(CultureInfo.InvariantCulture)} && test \"$(sha256sum {Quote(partial)} | cut -d' ' -f1)\" = {Quote(localHash.ToLowerInvariant())} && mv -f -- {Quote(partial)} {Quote(RemoteSourcePath)}", cancellationToken: cancellationToken);
        EnsureSuccess(verify, "Remote source verification");
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

    public async Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default)
    {
        var remoteOutput = $"{EncodeDirectory(attempt)}/output{Path.GetExtension(outputPath).ToLowerInvariant()}";
        var hashResult = await SshAsync($"sha256sum {Quote(remoteOutput)} | cut -d' ' -f1", cancellationToken: cancellationToken);
        EnsureSuccess(hashResult, "Remote output checksum");
        var expectedHash = hashResult.StandardOutput.Trim();
        if (!HashPattern().IsMatch(expectedHash)) throw new InvalidDataException("Remote output checksum was invalid.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var partial = outputPath + ".partial";
        if (File.Exists(outputPath) && string.Equals(await HashFileAsync(outputPath, cancellationToken), expectedHash, StringComparison.OrdinalIgnoreCase)) return;
        await SftpAsync($"reget {SftpQuote(remoteOutput)} {SftpQuote(partial)}\n", cancellationToken);
        var actualHash = await HashFileAsync(partial, cancellationToken);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            await SftpAsync($"reget {SftpQuote(remoteOutput)} {SftpQuote(partial)}\n", cancellationToken);
            actualHash = await HashFileAsync(partial, cancellationToken);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException("Downloaded output checksum does not match the remote output after a clean retry.");
            }
        }
        File.Move(partial, outputPath, overwrite: true);
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
                result = await _processes.RunAsync(new ExternalProcessRequest(_settings.Tools.SshPath, ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", RemoteHost!, "bash -lc " + Quote(command)], standardInput), cancellationToken);
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

    private async Task SftpAsync(string batch, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ExternalProcessResult result;
            try
            {
                result = await _processes.RunAsync(new ExternalProcessRequest(_settings.Tools.SftpPath, ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-b", "-", RemoteHost!], batch), cancellationToken);
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

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static async Task<string> HashFilePrefixAsync(string path, long length, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) throw new EndOfStreamException("Source became shorter while validating the resumable upload.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    [GeneratedRegex(@"^/.+?/[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidWorkspacePattern();
    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
