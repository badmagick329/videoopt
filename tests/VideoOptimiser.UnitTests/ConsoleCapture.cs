using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Cli;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// Runs the real CLI with its console captured. The capture is <see cref="CliConsole.Redirect"/>, which is scoped to the
/// running test's async flow and leaves the process-wide <see cref="Console"/> alone, so these tests run in parallel.
/// </summary>
internal static class ConsoleCapture
{
    public static Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(params string[] args) => RunCliAsync(null, args);

    /// <param name="configureServices">Replaces real services with fakes; see <see cref="CliApplication.RunAsync"/>.</param>
    public static Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(Action<IServiceCollection>? configureServices, params string[] args) => RunCliWithInputAsync(null, configureServices, args);

    /// <param name="input">What the user types, such as "y" for a [y/N] prompt; null is no input at all, which declines.</param>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliWithInputAsync(string? input, Action<IServiceCollection>? configureServices, params string[] args)
    {
        using var error = new StringWriter();
        using var output = new StringWriter();
        using var scope = CliConsole.Redirect(output, error, input is null ? TextReader.Null : new StringReader(input));
        var code = await CliApplication.RunAsync(args, services =>
        {
            CliFakes.NoSideEffects(services);
            configureServices?.Invoke(services);
        });
        return (code, output.ToString(), error.ToString());
    }
}
