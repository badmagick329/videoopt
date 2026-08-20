using System.ComponentModel;
using System.Diagnostics;
using VideoOptimiser.Application.Diagnostics;

namespace VideoOptimiser.Infrastructure.Diagnostics;

public sealed class ProcessToolVerifier : IToolVerifier
{
    public async Task<ToolVerificationResult> VerifyAsync(string name, string executable, string versionArgument, CancellationToken cancellationToken = default)
    {
        return await RunAsync(name, executable, versionArgument, requireSuccessfulExit: true, cancellationToken);
    }

    public async Task<ToolVerificationResult> VerifyPresenceAsync(string name, string executable, string availabilityArgument, CancellationToken cancellationToken = default)
    {
        return await RunAsync(name, executable, availabilityArgument, requireSuccessfulExit: false, cancellationToken);
    }

    private static async Task<ToolVerificationResult> RunAsync(
        string name,
        string executable,
        string argument,
        bool requireSuccessfulExit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new ToolVerificationResult(name, false, "No executable was configured.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ToolVerificationResult(name, false, $"Could not start '{executable}'.");
            }

            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await standardOutputTask).Trim();
            var error = (await standardErrorTask).Trim();

            if (requireSuccessfulExit && process.ExitCode != 0)
            {
                return new ToolVerificationResult(name, false, $"'{executable} {argument}' exited with {process.ExitCode}: {FirstLine(error)}");
            }

            var detail = FirstLine(string.IsNullOrWhiteSpace(output) ? error : output);
            return new ToolVerificationResult(name, true, requireSuccessfulExit ? detail : $"Executable started successfully. {detail}");
        }
        catch (Win32Exception exception)
        {
            return new ToolVerificationResult(name, false, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ToolVerificationResult(name, false, exception.Message);
        }
    }

    private static string FirstLine(string value)
    {
        var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "Version command completed successfully." : line;
    }
}
