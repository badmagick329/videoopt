using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests;

/// <summary>Fakes for running the real CLI through <see cref="ConsoleCapture"/> without Hetzner, SSH or the media tools.</summary>
internal static class CliFakes
{
    public static readonly RemoteWorkerInfo Worker = new(42, "video-optimiser-1", "203.0.113.10", "running", null, "cx43", "hel1");

    /// <summary>Settings validation checks real files (keys, tools); these tests are about what a run prints.</summary>
    public static void AcceptAnySettings(IServiceCollection services) => services.AddSingleton<ISettingsValidator, NoDiagnostics>();

    /// <summary>Never asks: a test must not wait on the console.</summary>
    public static void NeverConfirm(IServiceCollection services) => services.AddSingleton<IInteractiveConfirmation, NoConfirmation>();

    private sealed class NoDiagnostics : ISettingsValidator
    {
        public IReadOnlyList<Diagnostic> Validate(AppSettings settings) => [];
    }

    private sealed class NoConfirmation : IInteractiveConfirmation
    {
        public bool IsAvailable => false;
        public bool Confirm(string prompt) => false;
    }
}

/// <summary>A managed worker that always exists. Managed exactly when the real lifecycle would be: remote mode and the Hetzner lifecycle.</summary>
internal sealed class FakeWorkerLifecycle : IRemoteWorkerLifecycle
{
    public int EnsureCalls { get; private set; }
    public int DeleteCalls { get; private set; }

    public bool IsManaged(AppSettings settings) =>
        settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(settings.Processing.RemoteSsh.Lifecycle, RemoteLifecycleModes.Hetzner, StringComparison.OrdinalIgnoreCase);

    public Task<RemoteWorkerInfo> EnsureReadyAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, Func<RemoteWorkerCreationQuote, CancellationToken, Task<bool>>? confirmCreation = null, CancellationToken cancellationToken = default)
    {
        EnsureCalls++;
        return Task.FromResult(CliFakes.Worker);
    }

    public Task<RemoteWorkerInfo?> GetAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default) => Task.FromResult<RemoteWorkerInfo?>(CliFakes.Worker);

    public Task<RemoteWorkerInfo?> ReadRecordedAsync(string databasePath, CancellationToken cancellationToken = default) => Task.FromResult<RemoteWorkerInfo?>(CliFakes.Worker);

    public Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        DeleteCalls++;
        return Task.FromResult(true);
    }
}
