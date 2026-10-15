using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests;

/// <summary>Fakes for running the real CLI through <see cref="ConsoleCapture"/> without Hetzner, SSH or the media tools.</summary>
internal static class CliFakes
{
    public static readonly RemoteWorkerInfo Worker = new(42, "video-optimiser-1", "203.0.113.10", "running", null, "cx43", "hel1");

    /// <summary>Settings validation checks real files (keys, tools); these tests are about what a run prints.</summary>
    public static void AcceptAnySettings(IServiceCollection services) => services.AddSingleton<ISettingsValidator, NoDiagnostics>();

    /// <summary>Redirected input: the worker question is never asked, and a test never waits on the console.</summary>
    public static void NotInteractive(IServiceCollection services) => services.AddSingleton<IConsoleInput>(new ScriptedInput(interactive: false));

    private sealed class NoDiagnostics : ISettingsValidator
    {
        public IReadOnlyList<Diagnostic> Validate(AppSettings settings) => [];
    }
}

/// <summary>
/// What the user types, line by line. A line queued with <see cref="Later"/> arrives only when the test calls
/// <see cref="Type"/>, as a user who answers while the video keeps converting; with nothing queued, input has ended (null).
/// </summary>
internal sealed class ScriptedInput(bool interactive = true) : IConsoleInput
{
    private readonly Queue<TaskCompletionSource<string?>> _lines = new();
    private readonly object _gate = new();
    private TaskCompletionSource<string?>? _later;

    public bool IsInteractive => interactive;
    public int Reads { get; private set; }

    /// <summary>A line typed at once when read.</summary>
    public ScriptedInput Then(string? line)
    {
        var typed = new TaskCompletionSource<string?>();
        typed.SetResult(line);
        lock (_gate) _lines.Enqueue(typed);
        return this;
    }

    /// <summary>A read that waits until <see cref="Type"/>.</summary>
    public ScriptedInput Later()
    {
        lock (_gate)
        {
            _later = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _lines.Enqueue(_later);
        }
        return this;
    }

    /// <summary>Answers the read <see cref="Later"/> queued; null is Ctrl+C ending it.</summary>
    public void Type(string? line) => _later!.SetResult(line);

    public string? ReadLine()
    {
        TaskCompletionSource<string?>? next;
        lock (_gate)
        {
            Reads++;
            next = _lines.Count > 0 ? _lines.Dequeue() : null;
        }
        return next?.Task.GetAwaiter().GetResult();
    }
}

/// <summary>
/// A managed worker, up until it is deleted and up again once a run creates it. Managed exactly when the real lifecycle would
/// be: remote mode and the Hetzner lifecycle. Its delete prints the real lifecycle's lines.
/// </summary>
internal sealed class FakeWorkerLifecycle : IRemoteWorkerLifecycle
{
    public int EnsureCalls { get; private set; }
    public int DeleteCalls { get; private set; }

    /// <summary>The database path each create, status and delete was given; it picks the state file, the lock and the server's owner label.</summary>
    public List<string> DatabasePaths { get; } = [];

    /// <summary>Whether the local state file records a worker.</summary>
    public bool Exists { get; set; } = true;

    /// <summary>Thrown by the delete, after its first line, as a Hetzner error would be.</summary>
    public Exception? DeleteFailure { get; set; }

    /// <summary>Runs partway through the delete, such as a Ctrl+C.</summary>
    public Action? DuringDelete { get; set; }

    public bool IsManaged(AppSettings settings) =>
        settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(settings.Processing.RemoteSsh.Lifecycle, RemoteLifecycleModes.Hetzner, StringComparison.OrdinalIgnoreCase);

    public Task<RemoteWorkerInfo> EnsureReadyAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, Func<RemoteWorkerCreationQuote, CancellationToken, Task<bool>>? confirmCreation = null, CancellationToken cancellationToken = default)
    {
        EnsureCalls++;
        DatabasePaths.Add(databasePath);
        Exists = true;
        return Task.FromResult(CliFakes.Worker);
    }

    public Task<RemoteWorkerInfo?> GetAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default)
    {
        DatabasePaths.Add(databasePath);
        return ReadRecordedAsync(databasePath, cancellationToken);
    }

    /// <summary>Thrown when the state file is read, as an unreadable one would be.</summary>
    public Exception? ReadFailure { get; set; }

    public Task<RemoteWorkerInfo?> ReadRecordedAsync(string databasePath, CancellationToken cancellationToken = default) =>
        ReadFailure is null ? Task.FromResult(Exists ? CliFakes.Worker : null) : Task.FromException<RemoteWorkerInfo?>(ReadFailure);

    public Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteCalls++;
        DatabasePaths.Add(databasePath);
        if (!Exists) return Task.FromResult(false);
        progress?.Report($"Deleting Hetzner server {CliFakes.Worker.ServerId}.");
        DuringDelete?.Invoke();
        if (DeleteFailure is not null) throw DeleteFailure;
        Exists = false;
        progress?.Report("Managed Hetzner worker deleted; billing for its server and Primary IPv4 has stopped.");
        return Task.FromResult(true);
    }
}
