using System.Diagnostics;

namespace VideoOptimiser.Infrastructure.Processing;

public sealed record ExternalProcessRequest(string FileName, IReadOnlyList<string> Arguments, string? StandardInput = null);
public sealed record ExternalProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IExternalProcessRunner
{
    Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default);
}

public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    public async Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            RedirectStandardInput = request.StandardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Could not start '{request.FileName}'.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _ = exception;
            }
            throw;
        }

        return new ExternalProcessResult(process.ExitCode, await outputTask, await errorTask);
    }
}
