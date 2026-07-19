using System.ComponentModel;
using System.Diagnostics;
using VideoOptimiser.Application.Diagnostics;

namespace VideoOptimiser.Infrastructure.Diagnostics;

public sealed class ProcessToolVerifier : IToolVerifier
{
    public async Task<ToolVerificationResult> VerifyAsync(string name, string executable, string versionArgument, CancellationToken cancellationToken = default)
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
        startInfo.ArgumentList.Add(versionArgument);

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

            if (process.ExitCode != 0)
            {
                return new ToolVerificationResult(name, false, $"'{executable} {versionArgument}' exited with {process.ExitCode}: {FirstLine(error)}");
            }

            return new ToolVerificationResult(name, true, FirstLine(string.IsNullOrWhiteSpace(output) ? error : output));
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
