using System.Runtime.InteropServices;
using System.Text;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>The Windows console calls the TUI needs: ANSI output on, and the code page to UTF-8.</summary>
internal static partial class NativeConsole
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);

    /// <summary>Turns ANSI processing on for stdout and returns the mode to put back, or null if it was not a console.</summary>
    public static uint? EnableAnsiOutput()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (!GetConsoleMode(handle, out var mode)) return null;
        SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        return mode;
    }

    public static void RestoreOutputMode(uint mode) => SetConsoleMode(GetStdHandle(StdOutputHandle), mode);
}

/// <summary>
/// The real terminal: alternate screen, hidden cursor, raw Ctrl+C, and the one place that undoes all of it. Restore is
/// idempotent and is reached from the normal exit, from a finally, from Console.CancelKeyPress (Ctrl+Break) and from
/// ProcessExit, so no exit path leaves the user in the alternate screen with no cursor.
/// </summary>
internal sealed class TerminalSession
{
    private const string Esc = "\u001b";
    private readonly Stream _out = Console.OpenStandardOutput();
    private int _restored;
    private uint? _originalMode;
    private Encoding? _originalEncoding;

    public void Enter()
    {
        _originalEncoding = Console.OutputEncoding;
        Console.OutputEncoding = new UTF8Encoding(false);
        _originalMode = NativeConsole.EnableAnsiOutput();
        Console.TreatControlCAsInput = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
        // Alternate screen, hide cursor, no auto-wrap (a row of exactly the window's width must not wrap).
        Write($"{Esc}[?1049h{Esc}[?25l{Esc}[?7l");
    }

    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _out.Write(bytes, 0, bytes.Length);
        _out.Flush();
    }

    public void Restore()
    {
        if (Interlocked.Exchange(ref _restored, 1) == 1) return;
        try
        {
            Write($"{Esc}[0m{Esc}[?7h{Esc}[?25h{Esc}[?1049l");
            Console.TreatControlCAsInput = false;
            if (_originalMode is { } mode) NativeConsole.RestoreOutputMode(mode);
            if (_originalEncoding is not null) Console.OutputEncoding = _originalEncoding;
        }
        catch (IOException)
        {
            // The console is already gone.
        }
    }
}
