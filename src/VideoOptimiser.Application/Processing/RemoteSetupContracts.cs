using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Processing;

/// <summary>
/// The one-time setup for managed Hetzner workers: token check, SSH key, and the <c>processing:</c> config section.
/// It creates no server, so it costs nothing; the only Hetzner write is the SSH key upload.
/// </summary>
public interface IRemoteSetupService
{
    /// <param name="confirm">Asks the user the prompt it is given; false declines. Throws on cancellation.</param>
    /// <param name="output">Receives each line the user should see, including the config block.</param>
    Task<ExitCode> RunAsync(
        LoadedConfiguration configuration,
        Func<string, CancellationToken, Task<bool>> confirm,
        IProgress<string> output,
        CancellationToken cancellationToken);
}
