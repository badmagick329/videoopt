using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Tools;
using static VideoOptimiser.Infrastructure.Features.Run.RemoteShell;
using static VideoOptimiser.Infrastructure.Tools.PosixShell;

namespace VideoOptimiser.Infrastructure.Features.Run;

/// <summary>
/// One job's processing on a remote worker over SSH: the resumable, checksum-verified transfers and the workspace, composed
/// with the transport (<see cref="RemoteShell"/>, whose contract says which commands may be resent) and the detached stages
/// (<see cref="RemoteStageRunner"/>, which owns a stage's files).
/// </summary>
public sealed partial class RemoteSshProcessingSession : IProcessingSession
{
    /// <summary>A step that has run this long prints its first progress line, and then at most one per interval.</summary>
    internal static readonly TimeSpan ProgressDelay = TimeSpan.FromSeconds(10);

    private readonly JobRecord _job;
    private readonly AppSettings _settings;
    private readonly RemoteShell _shell;
    private readonly RemoteStageRunner _stages;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<CancellationToken, Task> _transferPollDelay;
    private readonly string _root;

    public RemoteSshProcessingSession(JobRecord job, AppSettings settings, IExternalProcessRunner processes)
        : this(job, settings, processes, cancellationToken => Task.Delay(TimeSpan.FromSeconds(10), cancellationToken))
    {
    }

    /// <param name="pollDelay">The wait between polls of a running remote stage.</param>
    /// <param name="clock">Elapsed time, for timing transfers and deciding when a progress line is due; tests advance it by hand.</param>
    /// <param name="transferPollDelay">The wait between checks of a running transfer; each due check of an upload is an SSH connection.</param>
    /// <param name="commandTimeouts">How long each try of one SSH command may take; <see cref="RemoteShell.DefaultCommandTimeouts"/> unless a test shortens them.</param>
    /// <param name="retryDelay">The pause before another try of an SSH command or an sftp transfer; a second unless a test removes it.</param>
    internal RemoteSshProcessingSession(JobRecord job, AppSettings settings, IExternalProcessRunner processes, Func<CancellationToken, Task> pollDelay, Func<TimeSpan>? clock = null, Func<CancellationToken, Task>? transferPollDelay = null, IReadOnlyList<TimeSpan>? commandTimeouts = null, Func<CancellationToken, Task>? retryDelay = null)
    {
        _job = job;
        _settings = settings;
        _clock = clock ?? StartClock();
        _transferPollDelay = transferPollDelay ?? (cancellationToken => Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
        _shell = new RemoteShell(settings, processes, commandTimeouts ?? RemoteShell.DefaultCommandTimeouts, retryDelay ?? (cancellationToken => Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)));
        // The validator rejects a bad value in remote mode; a mode given for one run (--remote) can reach here unchecked.
        if (!HumanReadableValues.TryParseDuration(settings.Processing.RemoteSsh.StageSilenceTimeout, out var stageSilenceTimeout) || stageSilenceTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException(SettingsValidator.StageSilenceTimeoutMessage);
        _root = settings.Processing.RemoteSsh.WorkingDirectory.TrimEnd('/');
        RemoteWorkspace = $"{_root}/{job.Id:N}";
        if (!GuidWorkspacePattern().IsMatch(RemoteWorkspace)) throw new InvalidOperationException("The generated remote workspace is not a safe GUID workspace.");
        _stages = new RemoteStageRunner(_shell, job.Id, RemoteWorkspace, stageSilenceTimeout, pollDelay, _clock, CleanupAsync);
    }

    private static Func<TimeSpan> StartClock()
    {
        var stopwatch = Stopwatch.StartNew();
        return () => stopwatch.Elapsed;
    }

    public bool IsRemote => true;
    public string ExecutionMode => ProcessingModes.RemoteSsh;
    public string? RemoteHost => _shell.Host;
    public string? RemoteWorkspace { get; }

    internal string RemoteSourcePath => $"{RemoteWorkspace}/source{Path.GetExtension(_job.SourcePath).ToLowerInvariant()}";
    /// <summary>The source's checksum, written once the verified upload became the source (see <see cref="BuildVerifyAndRenameCommand"/>).</summary>
    internal string RemoteSourceChecksumPath => RemoteSourcePath + ".sha256";
    internal string CrfDirectory => $"{RemoteWorkspace}/crf";
    internal string EncodeDirectory(int attempt) => $"{RemoteWorkspace}/encode-{attempt.ToString(CultureInfo.InvariantCulture)}";
    private string RemoteOutputPath(int attempt, string outputPath) => $"{EncodeDirectory(attempt)}/output{Path.GetExtension(outputPath).ToLowerInvariant()}";

    public Task StageAsync(string sourcePath, string sourceFingerprint, CancellationToken cancellationToken = default) =>
        StageAsync(sourcePath, sourceFingerprint, null, cancellationToken);

    public async Task StageAsync(string sourcePath, string sourceFingerprint, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default)
    {
        // Every check below is silent: a phone video's hash and the SSH round trips take a second or two, and a line per
        // check buried the lines that matter. Only the transfer, which can take minutes, announces itself and reports.
        var source = new FileInfo(sourcePath);
        var requiredDisk = CalculateRequiredDisk(source.Length, _settings.Processing.RemoteSsh.MinimumFreeDiskMultiplier);
        var minimumMemory = HumanReadableValues.TryParseSize(_settings.Processing.RemoteSsh.MinimumAvailableMemory, out var parsedMemory) ? parsedMemory : 0;
        var preflight = await _shell.SshAsync($"mkdir -p -- {Quote(RemoteWorkspace!)} && printf '%s %s %s' \"$(nproc)\" \"$(awk '/MemAvailable:/ {{printf \"%.0f\", $2 * 1024}}' /proc/meminfo)\" \"$(df -B1 --output=avail {Quote(RemoteWorkspace!)} | tail -n 1)\"", progress: progress, cancellationToken: cancellationToken);
        var values = preflight.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length != 3 || !int.TryParse(values[0], CultureInfo.InvariantCulture, out var cpus) || !long.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var memory) || !long.TryParse(values[2], CultureInfo.InvariantCulture, out var disk))
            throw new InvalidDataException("Remote resource preflight returned an unexpected response.");
        if (cpus < _settings.Processing.RemoteSsh.MinimumCpuCount) throw new InvalidOperationException($"Remote host has {cpus} CPUs; {_settings.Processing.RemoteSsh.MinimumCpuCount} are required.");
        if (memory < minimumMemory) throw new InvalidOperationException($"Remote host has {memory} available bytes; {minimumMemory} are required.");

        var localHash = await HashFileAsync(sourcePath, "Checking the original's checksum before upload.", progress, cancellationToken);
        var existing = await _shell.SshAsync($"if test -f {Quote(RemoteSourcePath)}; then stat -c '%s' {Quote(RemoteSourcePath)}; sha256sum {Quote(RemoteSourcePath)} | cut -d' ' -f1; fi", progress: progress, cancellationToken: cancellationToken);
        var existingParts = existing.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (existingParts.Length == 2 && existingParts[0] == source.Length.ToString(CultureInfo.InvariantCulture) && string.Equals(existingParts[1], localHash, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(JobProgress.Step("Already on the worker, checksum verified."));
            return;
        }
        if (disk < requiredDisk) throw new InvalidOperationException($"Remote host has {disk} free bytes; {requiredDisk} are required for this original.");

        var partial = RemoteSourcePath + ".partial";
        var verifiedPartialLength = await VerifyPartialUploadAsync(sourcePath, partial, source.Length, progress, cancellationToken);
        var size = HumanReadableValues.FormatSize(source.Length);
        var started = _clock();
        var transferred = false;
        if (verifiedPartialLength < source.Length)
        {
            progress?.Report(JobProgress.Step(verifiedPartialLength > 0
                ? $"Resuming the upload at {HumanReadableValues.FormatSize(verifiedPartialLength)} of {size}."
                : $"Uploading {size} to the worker."));
        }
        // The retry lives here, not in RemoteShell.SftpAsync, because each attempt must resume from what reached the worker:
        // resending the same `put` would upload the whole original again. Each attempt gets its own monitor, so a rate never
        // counts the stall.
        for (var attempt = 1; verifiedPartialLength < source.Length; attempt++)
        {
            transferred = true;
            var transferCommand = verifiedPartialLength > 0 ? "reput" : "put";
            var transfer = await TransferAsync($"{transferCommand} {SftpQuote(sourcePath)} {SftpQuote(partial)}\n",
                new TransferMonitor(new ThrottledProgress(progress, _clock, source.Length, verifiedPartialLength, showRate: true), verifiedPartialLength, async token => await RemoteFileSizeAsync(partial, token)),
                retryLostConnection: false, progress, cancellationToken);
            if (transfer.ExitCode == 0) break;
            if (!LostConnection(transfer))
            {
                // The dropped session can finish the partial after the re-check, and reput then refuses the equal-size destination.
                if (attempt == 1 || await VerifyPartialUploadAsync(sourcePath, partial, source.Length, progress, cancellationToken) != source.Length)
                    throw SftpTransferFailed(transfer);
                break;
            }
            if (attempt >= TransferAttempts) throw LostSftpConnection(transfer);
            // A drop after the last byte leaves a complete partial, which reput would refuse; it goes straight to the verify step.
            verifiedPartialLength = await VerifyPartialUploadAsync(sourcePath, partial, source.Length, progress, cancellationToken);
            if (verifiedPartialLength == source.Length) break;
            progress?.Report(JobProgress.Step(verifiedPartialLength > 0
                ? $"Lost the connection to the worker; resuming the upload at {HumanReadableValues.FormatSize(verifiedPartialLength)} of {size}."
                : $"Lost the connection to the worker; uploading {size} again from the start."));
        }
        var verify = await _shell.SshAsync(BuildVerifyAndRenameCommand(partial, source.Length, localHash), progress: progress, cancellationToken: cancellationToken);
        EnsureSuccess(verify, "Remote original verification");
        // A complete partial left by an earlier run needed no transfer now, so there is no duration to report.
        progress?.Report(JobProgress.Step(transferred
            ? $"Uploaded {size} in {HumanReadableValues.FormatDuration(_clock() - started)}, checksum verified."
            : "Already on the worker, checksum verified."));
    }

    /// <summary>
    /// Checks the complete partial upload's size and checksum, then renames it to the source, so a wrong file never becomes it.
    /// <para>It must be safe to resend (<see cref="IRemoteShell"/>): a checksum of a large original can outlast a try, and the
    /// cut-off try keeps running on the worker and does the rename, so its resend finds no partial. The checksum file beside the
    /// source says the rename already happened, at the cost of a read of 64 bytes rather than of the whole source again. The
    /// last line also covers a resend that arrived while the cut-off try was still hashing: both hash the partial, and the later
    /// one's rename fails once the earlier one has done it.</para>
    /// <para>The invariant: a checksum file holding a hash means the source has that hash. It is written only after a rename,
    /// and one holding another hash is removed before the rename, so a rename whose checksum write then fails (a full disk) leaves
    /// none that a later run could trust for a source it no longer describes. One that already holds this hash is kept, because a
    /// concurrent try may just have written it. The workspace is per job, so no other job's checksum file is ever there.</para>
    /// </summary>
    /// <remarks>One line, like every other command here: it travels as a single ssh argument through the Windows command line.</remarks>
    internal string BuildVerifyAndRenameCommand(string partial, long length, string hash) =>
        $"s={Quote(RemoteSourcePath)}; p={Quote(partial)}; m={Quote(RemoteSourceChecksumPath)}; h={Quote(hash.ToLowerInvariant())}; "
        + "verified() { test -f \"$s\" && test \"$(cat -- \"$m\" 2>/dev/null)\" = \"$h\"; }; "
        + "if verified; then exit 0; fi; "
        + $"if test \"$(stat -c '%s' -- \"$p\")\" = {length.ToString(CultureInfo.InvariantCulture)} && test \"$(sha256sum -- \"$p\" | cut -d' ' -f1)\" = \"$h\"; then "
        + "test \"$(cat -- \"$m\" 2>/dev/null)\" = \"$h\" || rm -f -- \"$m\" || exit 1; "
        + "mv -f -- \"$p\" \"$s\" && printf '%s\\n' \"$h\" > \"$m\" && exit 0; "
        + "fi; verified";

    /// <summary>
    /// Returns how many bytes of the worker's partial upload are a verified prefix of the original, deleting a partial that is not.
    /// Only the stat'ed length is hashed: after a dropped connection the old session can still be appending, and hashing the
    /// whole file would then fail the prefix check and delete a good partial.
    /// </summary>
    private async Task<long> VerifyPartialUploadAsync(string sourcePath, string partial, long sourceLength, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var state = await _shell.SshAsync($"if test -f {Quote(partial)}; then n=$(stat -c '%s' {Quote(partial)}); printf '%s ' \"$n\"; head -c \"$n\" {Quote(partial)} | sha256sum | cut -d' ' -f1; fi", progress: progress, cancellationToken: cancellationToken);
        var parts = state.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return 0;
        if (parts.Length == 2 && long.TryParse(parts[0], CultureInfo.InvariantCulture, out var length) && length > 0 && length <= sourceLength
            && string.Equals(await HashFilePrefixAsync(sourcePath, length, "Checking the part of the original already uploaded.", progress, cancellationToken), parts[1], StringComparison.OrdinalIgnoreCase))
            return length;
        _ = await _shell.SshAsync($"rm -f -- {Quote(partial)}", progress: progress, cancellationToken: cancellationToken);
        return 0;
    }

    public async Task<CrfSearchResult> SearchCrfAsync(string sourcePath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var arguments = new AbAv1CrfSearchClient("ab-av1").BuildArguments(RemoteSourcePath, settings);
        var elapsed = Stopwatch.StartNew();
        var run = await _stages.RunAsync(CrfDirectory, "CRF search", arguments, sourcePath, progress, cancellationToken, failedLog => AbAv1Output.NoWorthwhileCrf(failedLog, settings));
        elapsed.Stop();
        var crf = CrfSearchOutputParser.Parse(run.Log);
        // A search that began under an earlier command has run for longer than this one watched it.
        return new CrfSearchResult(crf, run.Log, string.Empty, run.Reattached ? null : elapsed.Elapsed, CrfSearchOutputParser.TryParsePrediction(run.Log, crf, out var prediction) ? prediction : null);
    }

    public async Task<ProcessingStageResult> EncodeAsync(string sourcePath, string outputPath, int crf, QualitySettings settings, int attempt, AudioPlan audio, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var remoteOutput = RemoteOutputPath(attempt, outputPath);
        var arguments = new AbAv1VideoEncoder("ab-av1").BuildArguments(RemoteSourcePath, remoteOutput, crf, settings, audio);
        var elapsed = Stopwatch.StartNew();
        _ = await _stages.RunAsync(EncodeDirectory(attempt), "encode", arguments, sourcePath, progress, cancellationToken);
        elapsed.Stop();
        var check = await _shell.SshAsync($"test -s {Quote(remoteOutput)}", progress: progress, cancellationToken: cancellationToken);
        EnsureSuccess(check, "Remote encoded output verification");
        return new ProcessingStageResult(outputPath, elapsed.Elapsed);
    }

    public Task<bool> HasFinishedEncodeAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) =>
        _stages.HasFinishedEncodeAsync(EncodeDirectory(attempt), RemoteOutputPath(attempt, outputPath), cancellationToken);

    public Task RetrieveOutputAsync(string outputPath, int attempt, CancellationToken cancellationToken = default) =>
        RetrieveOutputAsync(outputPath, attempt, null, cancellationToken);

    public async Task RetrieveOutputAsync(string outputPath, int attempt, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default)
    {
        var remoteOutput = RemoteOutputPath(attempt, outputPath);
        var hashResult = await _shell.SshAsync($"stat -c '%s' {Quote(remoteOutput)}; sha256sum {Quote(remoteOutput)} | cut -d' ' -f1", progress: progress, cancellationToken: cancellationToken);
        EnsureSuccess(hashResult, "Remote output checksum");
        var hashParts = hashResult.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hashParts.Length == 0) throw new InvalidDataException("Remote output checksum was missing.");
        var outputLength = hashParts.Length > 1 && long.TryParse(hashParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOutputLength) ? parsedOutputLength : 0;
        var expectedHash = hashParts[^1];
        if (!HashPattern().IsMatch(expectedHash)) throw new InvalidDataException("Remote output checksum was invalid.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var partial = outputPath + ".partial";
        if (File.Exists(outputPath) && string.Equals(await HashFileAsync(outputPath, "Checking the output already downloaded.", progress, cancellationToken), expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(JobProgress.Step("Already downloaded, checksum verified."));
            return;
        }
        // A partial longer than the output belongs to an earlier encode, for example one lost with a deleted worker. sftp
        // refuses to resume onto a local file larger than the remote one, which would fail the job for good.
        if (File.Exists(partial) && outputLength > 0 && new FileInfo(partial).Length > outputLength) File.Delete(partial);
        var partialStart = File.Exists(partial) && outputLength > 0
            ? Math.Min(new FileInfo(partial).Length, outputLength)
            : 0;
        var size = HumanReadableValues.FormatSize(outputLength);
        progress?.Report(JobProgress.Step(partialStart > 0
            ? $"Resuming the download at {HumanReadableValues.FormatSize(partialStart)} of {size}."
            : $"Downloading {size} from the worker."));
        var started = _clock();
        await DownloadAsync(remoteOutput, partial, outputLength, partialStart, progress, cancellationToken);
        const string checkingDownload = "Checking the downloaded output's checksum.";
        var actualHash = await HashFileAsync(partial, checkingDownload, progress, cancellationToken);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(JobProgress.Detail("Checksum mismatch; downloading again."));
            File.Delete(partial);
            await DownloadAsync(remoteOutput, partial, outputLength, 0, progress, cancellationToken);
            actualHash = await HashFileAsync(partial, checkingDownload, progress, cancellationToken);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException("Downloaded output checksum does not match the remote output after a clean retry.");
            }
        }
        File.Move(partial, outputPath, overwrite: true);
        progress?.Report(JobProgress.Step($"Downloaded {size} in {HumanReadableValues.FormatDuration(_clock() - started)}, checksum verified."));
    }

    private async Task DownloadAsync(string remoteOutput, string partial, long outputLength, long partialStart, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        await TransferAsync($"reget {SftpQuote(remoteOutput)} {SftpQuote(partial)}\n",
            new TransferMonitor(new ThrottledProgress(progress, _clock, outputLength, partialStart, showRate: true), partialStart, _ => Task.FromResult(File.Exists(partial) ? new FileInfo(partial).Length : 0)),
            retryLostConnection: true, progress, cancellationToken);

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        var expected = $"{_root}/{_job.Id:N}";
        if (!string.Equals(RemoteWorkspace, expected, StringComparison.Ordinal) || !GuidWorkspacePattern().IsMatch(expected)) throw new InvalidOperationException("Refusing unsafe remote workspace cleanup.");
        var result = await _shell.SshAsync($"rm -rf -- {Quote(expected)}", cancellationToken: cancellationToken);
        EnsureSuccess(result, "Remote cleanup");
    }

    public async Task StopAndRemoveWorkspacesAsync(IReadOnlySet<Guid> jobIds, CancellationToken cancellationToken = default)
    {
        // Decided here, from the worker's own listing: the shell below only acts on the names it is handed. A missing working
        // directory lists nothing.
        var listing = await _shell.SshAsync($"ls -1 -- {Quote(_root)} 2>/dev/null; true", cancellationToken: cancellationToken);
        var workspaces = listing.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => Guid.TryParseExact(name, "N", out var id) && id.ToString("N") == name && jobIds.Contains(id))
            .ToArray();
        if (workspaces.Length == 0) return;
        // Joined with explicit line feeds, as the stage script is: a literal spanning lines would carry the source file's line
        // endings, and a carriage return breaks bash.
        var stopAndRemove = string.Join("\n",
        [
            RemoteStageRunner.StopStageFunction,
            $"root={Quote(_root)}",
            "while read -r id; do",
            "case \"$id\" in ''|*[!0-9a-f]*) continue;; esac",
            "test \"${#id}\" -eq 32 || continue",
            "workspace=\"$root/$id\"",
            "test -d \"$workspace\" || continue",
            RemoteStageRunner.StopEveryStageIn("\"$workspace\""),
            "rm -rf -- \"$workspace\"",
            "done"
        ]);
        var removal = await _shell.SshAsync(stopAndRemove, string.Join("\n", workspaces) + "\n", cancellationToken: cancellationToken);
        EnsureSuccess(removal, "Remote workspace removal");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Runs one sftp batch through <see cref="RemoteShell.SftpAsync"/>, reporting the transfer's progress through
    /// <paramref name="monitor"/> while it runs.
    /// </summary>
    /// <param name="retryLostConnection">False returns any failed exit to the caller instead of resending the same batch or throwing.</param>
    private async Task<ExternalProcessResult> TransferAsync(string batch, TransferMonitor monitor, bool retryLostConnection, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var transfer = _shell.SftpAsync(batch, retryLostConnection, progress, cancellationToken);
        try
        {
            while (!transfer.IsCompleted)
            {
                await _transferPollDelay(cancellationToken);
                if (transfer.IsCompleted) break;
                await monitor.ReportAsync(cancellationToken);
            }

            return await transfer;
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

    internal static long CalculateRequiredDisk(long sourceBytes, double multiplier) => checked((long)Math.Ceiling(sourceBytes * multiplier));

    private async Task<long> RemoteFileSizeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await _shell.SshAsync($"if test -f {Quote(path)}; then stat -c '%s' {Quote(path)}; else printf '0'; fi", cancellationToken: cancellationToken);
        EnsureSuccess(result, "Remote file size check");
        return long.TryParse(result.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) && length >= 0 ? length : 0;
    }

    private async Task<string> HashFileAsync(string path, string step, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return await HashAsync(stream, stream.Length, step, progress, cancellationToken);
    }

    private async Task<string> HashFilePrefixAsync(string path, long length, string step, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return await HashAsync(stream, length, step, progress, cancellationToken);
    }

    /// <summary>
    /// Hashes the first <paramref name="length"/> bytes of <paramref name="stream"/>. Most hashes of a phone video take a
    /// second or two and print nothing; only one that outlasts <see cref="ProgressDelay"/> reports, so a multi-gigabyte
    /// original does not look hung. Its first report is <paramref name="step"/>, so the percentages have a step to sit under.
    /// </summary>
    private async Task<string> HashAsync(FileStream stream, long length, string step, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        var processed = 0L;
        var reporter = new ThrottledProgress(progress, _clock, length, 0, showRate: false, step);
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
    /// <param name="introduction">A step line printed before the first percentage, for work that has no step of its own.</param>
    private sealed class ThrottledProgress(IProgress<JobProgress>? progress, Func<TimeSpan> clock, long totalBytes, long initialBytes, bool showRate, string? introduction = null)
    {
        private readonly TimeSpan _start = clock();
        private TimeSpan _lastReport = clock();
        private bool _introduced;

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
                if (introduction is not null && !_introduced) progress!.Report(JobProgress.Step(introduction));
                _introduced = true;
                progress!.Report(JobProgress.Reading($"Checking the checksum: {percent}% of {HumanReadableValues.FormatSize(totalBytes)}", percent));
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
            progress!.Report(JobProgress.Reading(text, percent));
        }
    }

    [GeneratedRegex(@"^/.+?/[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidWorkspacePattern();
    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
