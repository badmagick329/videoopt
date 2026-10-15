using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Features.RemoteSetup;

/// <summary>
/// The one-time setup for managed Hetzner workers: token check, SSH key, and the <c>processing:</c> config section.
/// It creates no server, so it costs nothing; the only Hetzner write is the SSH key upload.
/// </summary>
public interface IRemoteSetupService
{
    /// <param name="confirm">Asks the user the prompt it is given; false declines. Throws on cancellation.</param>
    /// <param name="output">Receives each progress line the user should see, including the config block.</param>
    /// <param name="errors">Receives each line that explains why setup stopped or what is still wrong, so the caller can print it as an error.</param>
    Task<RemoteSetupResult> RunAsync(
        LoadedConfiguration configuration,
        Func<string, CancellationToken, Task<bool>> confirm,
        IProgress<string> output,
        IProgress<string> errors,
        CancellationToken cancellationToken);
}

/// <summary>How a setup run ended.</summary>
/// <param name="NextVerb">
/// The command to suggest, such as <c>run</c>, only when setup completed. The service cannot build the line itself: whether
/// <c>--config</c> appears depends on how the user typed the command, which only the CLI knows.
/// </param>
/// <param name="ConfigStillInvalid">
/// The edit was written and is valid on its own, but the file still has problems that were there before it, which the service
/// listed through <c>errors</c>. Setup is not complete then, so there is no <paramref name="NextVerb"/>.
/// </param>
public sealed record RemoteSetupResult(ExitCode ExitCode, string? NextVerb = null, bool ConfigStillInvalid = false);
