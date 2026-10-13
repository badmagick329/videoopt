using System.Runtime.InteropServices;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.Cli;

/// <summary>
/// Colours and dims console text, for one output stream. Colour only helps a person at a terminal: a redirected stream
/// (an agent reading the output, a log file) would hold escape codes as noise, so each stream decides for itself and the
/// writers receive the result. They take a <see cref="TerminalStyle"/> instead of asking the environment, which lets a test
/// force colour on or off with plain <see cref="StringWriter"/>s.
/// </summary>
internal sealed class TerminalStyle
{
    private const string Escape = "\u001b[";
    private const string Reset = "\u001b[0m";

    private readonly bool _enabled;

    private TerminalStyle(bool enabled) => _enabled = enabled;

    /// <summary>Text comes back unchanged.</summary>
    public static TerminalStyle Plain { get; } = new(false);

    /// <summary>Colours text with ANSI escape codes; for a stream already known to be a terminal that understands them.</summary>
    public static TerminalStyle Ansi { get; } = new(true);

    public string Bold(string text) => Wrap("1", text);

    public string Dim(string text) => Wrap("2", text);

    public string Green(string text) => Wrap("32", text);

    public string Yellow(string text) => Wrap("33", text);

    public string Red(string text) => Wrap("31", text);

    /// <summary>Green for success, yellow for a warning, red for a failure; <see cref="JobTone.None"/> is left as it is.</summary>
    public string Tone(JobTone tone, string text) => tone switch
    {
        JobTone.Success => Green(text),
        JobTone.Warning => Yellow(text),
        JobTone.Failure => Red(text),
        _ => text
    };

    // Empty text gets no codes, so a blank line stays blank. The reset follows every run so one line's colour never leaks into the next.
    private string Wrap(string code, string text) => _enabled && text.Length > 0 ? $"{Escape}{code}m{text}{Reset}" : text;

    /// <summary>
    /// Whether a stream should be coloured: only a terminal (not redirected), when the user has not opted out with
    /// <c>NO_COLOR</c> (set and not empty, per no-color.org) or a <c>TERM</c> of <c>dumb</c>, and never while the command
    /// writes <c>--json</c>, whose output a program reads.
    /// </summary>
    internal static bool IsColourWanted(bool redirected, string? noColor, string? term, bool json) =>
        !json && !redirected && string.IsNullOrEmpty(noColor) && !string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase);

    /// <summary>The style for standard output, from the real console and environment.</summary>
    public static TerminalStyle ForOutput(bool json) => Detect(Console.IsOutputRedirected, VirtualTerminal.StandardOutput, json);

    /// <summary>The style for standard error; it can be a terminal while standard output is a file, or the reverse.</summary>
    public static TerminalStyle ForError(bool json) => Detect(Console.IsErrorRedirected, VirtualTerminal.StandardError, json);

    private static TerminalStyle Detect(bool redirected, int streamHandle, bool json) =>
        IsColourWanted(redirected, Environment.GetEnvironmentVariable("NO_COLOR"), Environment.GetEnvironmentVariable("TERM"), json) &&
        VirtualTerminal.TryEnable(streamHandle)
            ? Ansi
            : Plain;
}

/// <summary>
/// Windows consoles ignore escape codes until virtual terminal processing is switched on for the stream's handle. When it
/// cannot be, the answer is no colour: a console that does not understand the codes would print them as text.
/// </summary>
internal static partial class VirtualTerminal
{
    internal const int StandardOutput = -11;
    internal const int StandardError = -12;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    public static bool TryEnable(int streamHandle)
    {
        // Other platforms' terminals take the codes as they are.
        if (!OperatingSystem.IsWindows()) return true;

        var handle = GetStdHandle(streamHandle);
        if (handle == IntPtr.Zero || handle == -1) return false;
        if (!GetConsoleMode(handle, out var mode)) return false;
        return (mode & EnableVirtualTerminalProcessing) != 0 || SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);
}
