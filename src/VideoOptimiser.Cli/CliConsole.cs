namespace VideoOptimiser.Cli;

/// <summary>
/// The console every command prints to and reads from. It is <see cref="Console"/> until <see cref="Redirect"/> swaps in
/// other streams for the current run. The override is held in <see cref="AsyncLocal{T}"/>, not in <c>Console.SetOut</c>,
/// because the console's streams are process-wide: with them, two runs in one process (parallel tests) would write into
/// each other's capture. An async-local value flows into the tasks and threads a command starts, so the static helpers
/// and the worker-question read see the writers of the run that started them without any writer being passed down.
/// </summary>
internal static class CliConsole
{
    private static readonly AsyncLocal<TextWriter?> OutOverride = new();
    private static readonly AsyncLocal<TextWriter?> ErrorOverride = new();
    private static readonly AsyncLocal<TextReader?> InOverride = new();

    public static TextWriter Out => OutOverride.Value ?? Console.Out;

    public static TextWriter Error => ErrorOverride.Value ?? Console.Error;

    public static TextReader In => InOverride.Value ?? Console.In;

    /// <summary>
    /// A stream that has been replaced is not a terminal, so it reads as redirected: no colour, no full-screen view, no
    /// worker question, and no Ctrl+C handler for a run that has no console to press it in.
    /// </summary>
    public static bool IsOutputRedirected => OutOverride.Value is not null || Console.IsOutputRedirected;

    public static bool IsErrorRedirected => ErrorOverride.Value is not null || Console.IsErrorRedirected;

    public static bool IsInputRedirected => InOverride.Value is not null || Console.IsInputRedirected;

    /// <summary>Whether any stream is replaced, so this run is not attached to the process's real console.</summary>
    public static bool IsReplaced => OutOverride.Value is not null || ErrorOverride.Value is not null || InOverride.Value is not null;

    public static void WriteLine() => Out.WriteLine();

    public static void WriteLine(string? text) => Out.WriteLine(text);

    public static void Write(string? text) => Out.Write(text);

    public static string? ReadLine() => In.ReadLine();

    /// <summary>
    /// Replaces the streams that are not null, for the current async flow, until the result is disposed, which restores
    /// what was there before. Dispose it from the flow that created it.
    /// </summary>
    public static IDisposable Redirect(TextWriter? output = null, TextWriter? error = null, TextReader? input = null)
    {
        var previous = new Scope(OutOverride.Value, ErrorOverride.Value, InOverride.Value);
        if (output is not null) OutOverride.Value = output;
        if (error is not null) ErrorOverride.Value = error;
        if (input is not null) InOverride.Value = input;
        return previous;
    }

    private sealed class Scope(TextWriter? output, TextWriter? error, TextReader? input) : IDisposable
    {
        public void Dispose()
        {
            OutOverride.Value = output;
            ErrorOverride.Value = error;
            InOverride.Value = input;
        }
    }
}
