using System.Globalization;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.Infrastructure.Tools;

public sealed class FfmpegDecodeTester(string ffmpegPath, IExternalProcessRunner processRunner) : IMediaDecodeTester
{
    public async Task<DecodeTestResult> DecodeAsync(string path, DecodeWindow window, CancellationToken cancellationToken = default)
    {
        var result = await processRunner.RunAsync(new ExternalProcessRequest(ffmpegPath, BuildArguments(path, window)), cancellationToken);

        // At -v error ffmpeg can report a corrupt frame and still exit 0, so any error output fails the test.
        var error = FirstLine(result.StandardError);
        return result.ExitCode == 0 && error is null
            ? new DecodeTestResult(true, null)
            : new DecodeTestResult(false, error ?? $"ffmpeg exited with {result.ExitCode}.");
    }

    internal static IReadOnlyList<string> BuildArguments(string path, DecodeWindow window)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-xerror" };
        if (window.StartSeconds is { } start) arguments.AddRange(["-ss", Format(start)]);
        if (window.DurationSeconds is { } duration) arguments.AddRange(["-t", Format(duration)]);
        arguments.AddRange(["-i", path, "-map", "0:v", "-map", "0:a?", "-f", "null", "-"]);
        return arguments;
    }

    private static string Format(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string? FirstLine(string message) =>
        message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
