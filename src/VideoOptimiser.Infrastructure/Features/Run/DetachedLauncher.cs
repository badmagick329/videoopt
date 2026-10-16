using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using VideoOptimiser.Application.Features.Run;

namespace VideoOptimiser.Infrastructure.Features.Run;

/// <summary>
/// Starts this exe again so it outlives the terminal that launched it. Settled by Trial B (see the UX redesign plan):
/// <c>DETACHED_PROCESS</c> gives it no console to be closed with, <c>CREATE_NEW_PROCESS_GROUP</c> keeps Ctrl+C aimed at the
/// terminal out of it, and nothing is inherited, so it holds none of the launcher's handles open. No
/// <c>CREATE_BREAKAWAY_FROM_JOB</c>: the trial showed it was not needed, and a job that forbids breakaway refuses the call.
/// </summary>
public sealed class DetachedLauncher : IDetachedLauncher
{
    public IDetachedProcess Launch(IReadOnlyList<string> arguments, string workingDirectory)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown.");
        // CreateProcessW may write into the command line, so it gets its own writable, terminated copy.
        var commandLine = (BuildCommandLine(exe, arguments) + '\0').ToCharArray();
        var startup = new ProcessNativeMethods.StartupInfo { Cb = (uint)Marshal.SizeOf<ProcessNativeMethods.StartupInfo>() };
        if (!ProcessNativeMethods.CreateProcess(
                exe, ref commandLine[0], 0, 0, false,
                ProcessNativeMethods.DetachedProcess | ProcessNativeMethods.CreateNewProcessGroup,
                0, workingDirectory, ref startup, out var info))
        {
            throw new Win32Exception();
        }
        // The main thread's handle is of no use here, and an open one would outlive the need.
        _ = ProcessNativeMethods.CloseHandle(info.Thread);
        return new DetachedProcess(info.Process, (int)info.ProcessId);
    }

    /// <summary>The full command line, the exe first, quoted so the child's argument parser (<c>CommandLineToArgvW</c>) gets back exactly these strings.</summary>
    internal static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder(QuoteArgument(executable));
        foreach (var argument in arguments) line.Append(' ').Append(QuoteArgument(argument));
        return line.ToString();
    }

    /// <summary>
    /// An argument unchanged when it is non-empty and has nothing to split or quote on; otherwise in quotes. Inside quotes a
    /// backslash is literal unless a quote follows it, so a run of backslashes before a quote (or before the closing quote)
    /// is doubled, and the quote itself is escaped.
    /// </summary>
    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0) return argument;

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                quoted.Append('\\', backslashes);
            }
            backslashes = 0;
            quoted.Append(character);
        }
        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }

    private sealed class DetachedProcess(nint handle, int processId) : IDetachedProcess
    {
        private nint _handle = handle;

        public int ProcessId { get; } = processId;

        public int? WaitForExit(TimeSpan timeout)
        {
            var milliseconds = (uint)Math.Clamp((long)timeout.TotalMilliseconds, 0, ProcessNativeMethods.Infinite - 1);
            var result = ProcessNativeMethods.WaitForSingleObject(_handle, milliseconds);
            if (result == ProcessNativeMethods.WaitTimeout) return null;
            if (result != ProcessNativeMethods.WaitObject0 || !ProcessNativeMethods.GetExitCodeProcess(_handle, out var exitCode)) throw new Win32Exception();
            return unchecked((int)exitCode);
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, 0);
            if (handle != 0) _ = ProcessNativeMethods.CloseHandle(handle);
        }
    }
}
