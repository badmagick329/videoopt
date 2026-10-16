using Microsoft.Extensions.DependencyInjection;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// Runs the real CLI with the console captured. Console streams are process-wide, so every test class that uses this joins
/// <see cref="Collection"/>, which xUnit runs one test at a time.
/// </summary>
internal static class ConsoleCapture
{
    public const string Collection = "Console capture";

    public static Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(params string[] args) => RunCliAsync(null, args);

    /// <param name="configureServices">Replaces real services with fakes; see <see cref="CliApplication.RunAsync"/>.</param>
    public static Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(Action<IServiceCollection>? configureServices, params string[] args) => RunCliWithInputAsync(null, configureServices, args);

    /// <param name="input">What the user types, such as "y" for a [y/N] prompt; null is no input at all, which declines.</param>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliWithInputAsync(string? input, Action<IServiceCollection>? configureServices, params string[] args)
    {
        var originalError = Console.Error;
        var originalOut = Console.Out;
        var originalIn = Console.In;
        using var error = new StringWriter();
        using var output = new StringWriter();
        Console.SetError(error);
        Console.SetOut(output);
        Console.SetIn(input is null ? TextReader.Null : new StringReader(input));
        try
        {
            var code = await CliApplication.RunAsync(args, services =>
            {
                CliFakes.NoSideEffects(services);
                configureServices?.Invoke(services);
            });
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
        }
    }
}
