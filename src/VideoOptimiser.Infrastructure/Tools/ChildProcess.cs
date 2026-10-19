using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace VideoOptimiser.Infrastructure.Tools;

/// <summary>
/// Starts every tool child process (ffprobe, ab-av1, ffmpeg, ssh, sftp), so they all end the same way (VOPT-158):
/// <list type="bullet">
/// <item>Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never a joined string, so a path with spaces or
/// quotes reaches the tool as one argument.</item>
/// <item>Standard input is written inside the guard: a cancel or a broken pipe mid-write kills the child instead of leaving
/// ssh running with half its script.</item>
/// <item>Any failure while the child runs, cancellation included, kills its whole tree, waits briefly for it to exit, and
/// rethrows the original exception. A failing <c>Kill</c> is swallowed so the caller still sees the cancellation, which the
/// job processor turns into an interrupted job.</item>
/// <item>Both pipes are drained to the end even after a line callback throws; the callback's exception is rethrown once the
/// child has exited. A reader that stopped would let the child fill its pipe and block, and the wait would never end.</item>
/// </list>
/// </summary>
internal static class ChildProcess
{
    // Long enough for a killed tree to go, short enough that a cancel is never held up by a child that ignores the kill.
    private static readonly TimeSpan ExitWaitAfterKill = TimeSpan.FromSeconds(2);

    /// <summary>Runs to exit and returns both outputs verbatim (line endings included).</summary>
    public static Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(request, onStarted: null, cancellationToken);

    /// <inheritdoc cref="RunAsync(ExternalProcessRequest, CancellationToken)"/>
    /// <param name="onStarted">Given the child's process id once it is running, so a test can check the child is gone.</param>
    internal static async Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, Action<int>? onStarted, CancellationToken cancellationToken)
    {
        using var process = Start(request, onStarted);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await WaitGuardedAsync(process, request.StandardInput, outputTask, errorTask, cancellationToken);
        return new ExternalProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    /// <summary>
    /// Runs to exit, handing each line of standard output and standard error to its callback as it arrives (a null callback
    /// discards that stream), and returns the exit code. Each callback is called from one reader at a time, but the two
    /// streams' callbacks can run concurrently. After a callback throws, its stream's later lines are discarded.
    /// </summary>
    public static Task<int> StreamAsync(ExternalProcessRequest request, Action<string>? onOutputLine, Action<string>? onErrorLine, CancellationToken cancellationToken = default) =>
        StreamAsync(request, onOutputLine, onErrorLine, onStarted: null, cancellationToken);

    /// <inheritdoc cref="StreamAsync(ExternalProcessRequest, Action{string}?, Action{string}?, CancellationToken)"/>
    /// <param name="onStarted">Given the child's process id once it is running, so a test can check the child is gone.</param>
    internal static async Task<int> StreamAsync(ExternalProcessRequest request, Action<string>? onOutputLine, Action<string>? onErrorLine, Action<int>? onStarted, CancellationToken cancellationToken)
    {
        using var process = Start(request, onStarted);
        var outputTask = ReadLinesAsync(process.StandardOutput, onOutputLine, cancellationToken);
        var errorTask = ReadLinesAsync(process.StandardError, onErrorLine, cancellationToken);
        await WaitGuardedAsync(process, request.StandardInput, outputTask, errorTask, cancellationToken);
        await Task.WhenAll(outputTask, errorTask);
        return process.ExitCode;
    }

    private static Process Start(ExternalProcessRequest request, Action<int>? onStarted)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            RedirectStandardInput = request.StandardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Could not start '{request.FileName}'.");
        }

        onStarted?.Invoke(process.Id);
        return process;
    }

    private static async Task WaitGuardedAsync(Process process, string? standardInput, Task outputTask, Task errorTask, CancellationToken cancellationToken)
    {
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            await StopAsync(process);
            // The readers end when the killed child's pipes close, or fault when the process is disposed under them; either
            // way nobody awaits them now, so their faults are observed here rather than surfacing as unobserved exceptions.
            _ = Task.WhenAll(outputTask, errorTask).ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private static async Task StopAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Already exited, or the OS refused; the caller's own exception is the one worth reporting.
            return;
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(ExitWaitAfterKill, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // A child that outlives the kill is abandoned rather than allowed to hold up the caller.
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string>? onLine, CancellationToken cancellationToken)
    {
        ExceptionDispatchInfo? failure = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (onLine is null || failure is not null) continue;
            try
            {
                onLine(line);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        }

        failure?.Throw();
    }
}
