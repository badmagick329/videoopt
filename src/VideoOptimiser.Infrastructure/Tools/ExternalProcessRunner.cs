namespace VideoOptimiser.Infrastructure.Tools;

public sealed record ExternalProcessRequest(string FileName, IReadOnlyList<string> Arguments, string? StandardInput = null);
public sealed record ExternalProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IExternalProcessRunner
{
    Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default);
}

public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default) =>
        ChildProcess.RunAsync(request, cancellationToken);
}
