using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Cli;

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
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(Action<IServiceCollection>? configureServices, params string[] args)
    {
        var originalError = Console.Error;
        var originalOut = Console.Out;
        using var error = new StringWriter();
        using var output = new StringWriter();
        Console.SetError(error);
        Console.SetOut(output);
        try
        {
            var code = await CliApplication.RunAsync(args, configureServices);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetOut(originalOut);
        }
    }
}
