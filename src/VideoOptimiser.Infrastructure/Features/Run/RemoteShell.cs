using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.Infrastructure.Features.Run;

/// <summary>
/// Runs one command on the worker, resending it after a lost connection: exit 255, or no reply within the command timeout,
/// which ends that ssh.
/// <para><b>Every command sent through <see cref="SshAsync"/> is idempotent or guarded.</b> A try that is cut off locally
/// keeps running on the worker, and a reply can be lost after its command ran, so a resend can find the work already done,
/// or still under way. Reads, <c>mkdir -p</c>, <c>rm -f</c>, <c>rm -rf</c> and the upload of a stage script (to a name unique
/// per launch) repeat harmlessly. The rest are guarded:</para>
/// <list type="bullet">
/// <item>the stage launch (<see cref="RemoteStageRunner.BuildLaunchCommand"/>): a marker per launch, so a resend starts no
/// second script;</item>
/// <item>the upload's verify-and-rename (<see cref="RemoteSshProcessingSession.BuildVerifyAndRenameCommand"/>): the checksum
/// file beside the source says the rename already happened;</item>
/// <item>the stop of a stalled stage and the workspace sweep, through <c>stop_stage</c>
/// (<see cref="RemoteStageRunner.StopStageFunction"/>): it checks the pid's command line and always succeeds, and the stop
/// reads the exit code first.</item>
/// </list>
/// <para>A new command that changes state on the worker needs a guard of its own before it goes through here.</para>
/// </summary>
internal interface IRemoteShell
{
    /// <summary>Runs <paramref name="command"/> through <c>bash -lc</c> on the worker, resending it as the type says.</summary>
    /// <param name="standardInput">Sent to the command, for a script that would not fit one line.</param>
    Task<ExternalProcessResult> SshAsync(string command, string? standardInput = null, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// The ssh and sftp transport to the worker, with the timeouts and resends of each and the lines that report them. Every
/// remote command is one line, except a script sent on stdin: it travels as a single ssh argument through the Windows
/// command line.
/// </summary>
/// <param name="commandTimeouts">How long each try of one SSH command may take; <see cref="DefaultCommandTimeouts"/> unless a test shortens them.</param>
/// <param name="retryDelay">The pause before another try of an SSH command or an sftp transfer; a second unless a test removes it.</param>
internal sealed class RemoteShell(AppSettings settings, IExternalProcessRunner processes, IReadOnlyList<TimeSpan> commandTimeouts, Func<CancellationToken, Task> retryDelay) : IRemoteShell
{
    /// <summary>
    /// How long each try of one SSH command may take before it counts as a lost connection and is sent again; one entry per try.
    /// Keepalives catch a dead link, but not an ssh whose connection stays up while its command never returns: one such poll
    /// held a run for over half an hour after its encode had finished. Every command is a check, a launch or a poll that returns
    /// in a few seconds; the slowest, a checksum of a large video, takes seconds per GiB. Each try is longer than the one before
    /// because a command too slow for one limit is too slow for it again, and a longer try lets it finish; a hung command is
    /// usually answered by its first resend, so the longer tries cost time only when the worker is really gone. Three minutes
    /// in all before the worker counts as unreachable.
    /// </summary>
    internal static readonly IReadOnlyList<TimeSpan> DefaultCommandTimeouts = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90)];

    /// <summary>How many times one sftp transfer is tried, by <see cref="SftpAsync"/> or by an upload that resumes itself.</summary>
    internal const int TransferAttempts = 3;

    public string? Host => settings.Processing.RemoteSsh.Host;

    /// <inheritdoc/>
    /// <remarks>Each resend, and the answer that ends them, is a line on <paramref name="progress"/>: a resend takes 30 seconds or
    /// more, and without the lines a run that is recovering looks the same as one that is stuck.</remarks>
    public async Task<ExternalProcessResult> SshAsync(string command, string? standardInput = null, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var attempts = commandTimeouts.Count;
        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1) await retryDelay(cancellationToken);
            var nextTry = $"try {(attempt + 1).ToString(CultureInfo.InvariantCulture)} of {attempts.ToString(CultureInfo.InvariantCulture)}";
            ExternalProcessResult result;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(commandTimeouts[attempt - 1]);
            try
            {
                var arguments = OpenSshConnectionArguments.Build(settings.Processing.RemoteSsh);
                // -n: a command that sends nothing reads nothing, so ssh never holds the run's own stdin, which a
                // background run inherits from no console at all.
                if (standardInput is null) arguments.Add("-n");
                arguments.Add(Host!);
                arguments.Add("bash -lc " + PosixShell.Quote(command));
                result = await processes.RunAsync(new ExternalProcessRequest(settings.Tools.SshPath, arguments, standardInput), timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= attempts) throw new RemoteConnectionException($"The worker did not answer an SSH command {attempts} times in a row, {HumanReadableValues.FormatDuration(commandTimeouts.Aggregate(TimeSpan.Zero, (sum, wait) => sum + wait))} in all.");
                progress?.Report(JobProgress.Detail($"The worker didn't answer within {HumanReadableValues.FormatDuration(commandTimeouts[attempt - 1])}; asking again ({nextTry}).", JobTone.Warning));
                continue;
            }
            catch (Exception exception) when (exception is not OperationCanceledException && attempt >= attempts)
            {
                throw new RemoteConnectionException("Could not connect to the remote processing host.", exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                progress?.Report(JobProgress.Detail($"Couldn't run ssh ({FirstLine(exception.Message)}); trying again ({nextTry}).", JobTone.Warning));
                continue;
            }
            if (result.ExitCode != 255)
            {
                if (attempt > 1) progress?.Report(JobProgress.Detail("The worker answered again."));
                return result;
            }
            if (attempt >= attempts) throw new RemoteConnectionException($"SSH connection failed: {FirstLine(result.StandardError)}");
            progress?.Report(JobProgress.Detail($"Lost the connection to the worker ({FirstLine(result.StandardError)}); trying again ({nextTry}).", JobTone.Warning));
        }
    }

    private static string NextTry(int failedAttempt) =>
        string.Create(CultureInfo.InvariantCulture, $"try {failedAttempt + 1} of {TransferAttempts}");

    /// <summary>
    /// Runs one sftp batch, trying again when sftp cannot start. A transfer has no timeout: it can rightly take an hour, and the
    /// keepalives end one whose link died.
    /// </summary>
    /// <param name="retryLostConnection">True resends the same batch after a lost connection, which only a batch that resumes
    /// by itself (<c>reget</c>) may do; false returns any failed exit to the caller instead of resending it or throwing.</param>
    public async Task<ExternalProcessResult> SftpAsync(string batch, bool retryLostConnection, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            ExternalProcessResult result;
            try
            {
                var arguments = OpenSshConnectionArguments.Build(settings.Processing.RemoteSsh);
                arguments.Add("-b");
                arguments.Add("-");
                arguments.Add(Host!);
                result = await processes.RunAsync(new ExternalProcessRequest(settings.Tools.SftpPath, arguments, batch), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && attempt >= TransferAttempts)
            {
                throw new RemoteConnectionException("Could not connect to the remote processing host for file transfer.", exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                progress?.Report(JobProgress.Detail($"Couldn't run sftp ({FirstLine(exception.Message)}); trying again ({NextTry(attempt)}).", JobTone.Warning));
                await retryDelay(cancellationToken);
                continue;
            }
            if (result.ExitCode == 0 || !retryLostConnection) return result;
            if (!LostConnection(result)) throw SftpTransferFailed(result);
            if (attempt >= TransferAttempts) throw LostSftpConnection(result);
            progress?.Report(JobProgress.Detail($"Lost the connection to the worker ({FirstLine(result.StandardError)}); resuming the transfer ({NextTry(attempt)}).", JobTone.Warning));
            await retryDelay(cancellationToken);
        }
    }

    /// <summary>What OpenSSH prints, in whole or in part, when a connection dies under a running transfer.</summary>
    private static readonly string[] ConnectionLossMarkers = ["Connection closed", "Connection reset", "Broken pipe", "Connection timed out", "client_loop", "packet_write"];

    /// <summary>
    /// Whether a failed sftp lost its connection, so the job may resume, rather than failed for a reason a retry cannot change.
    /// Exit 255 is ssh's own failure and always counts. A drop under a running transfer can also end with status 1, which sftp
    /// uses for a refused file too, so the exit code alone would fail a job that only needs to reconnect (VOPT-83); stderr tells
    /// them apart. A refused file (permission denied, no space left, a missing path) matches none of the markers and stays a
    /// failure whose message carries sftp's own line, which names what to fix.
    /// </summary>
    internal static bool LostConnection(ExternalProcessResult result) =>
        result.ExitCode == 255 || ConnectionLossMarkers.Any(marker => result.StandardError.Contains(marker, StringComparison.OrdinalIgnoreCase));

    internal static InvalidOperationException SftpTransferFailed(ExternalProcessResult result) =>
        new($"SFTP transfer failed: {FirstLine(result.StandardError)}");

    internal static RemoteConnectionException LostSftpConnection(ExternalProcessResult result) =>
        new($"SFTP connection failed: {FirstLine(result.StandardError)}");

    /// <summary>
    /// Quotes a path for an sftp batch line. Inside quotes sftp escapes <c>?</c>, <c>[</c> and <c>*</c> itself before it globs
    /// (<c>makeargv</c> in OpenSSH's sftp.c), so a name such as <c>clip [1].mp4</c> matches only itself; adding backslashes here
    /// would make them literal backslashes in the name. A backslash inside quotes is kept and then eaten by the glob, hence
    /// Windows separators become <c>/</c>.
    /// </summary>
    internal static string SftpQuote(string value) => "\"" + value.Replace('\\', '/').Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    internal static void EnsureSuccess(ExternalProcessResult result, string operation)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException($"{operation} failed: {FirstLine(result.StandardError)}");
    }

    internal static string FirstLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "No error output.";
}
