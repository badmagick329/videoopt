using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Processing;

public sealed record RemoteWorkerInfo(long ServerId, string Name, string Address, string Status, DateTimeOffset? CreatedUtc);

/// <summary>
/// Indicates that the provider currently has no capacity in the configured
/// locations, either at preflight or after placement attempts.
/// </summary>
public sealed class RemoteWorkerCapacityUnavailableException : InvalidOperationException
{
    public RemoteWorkerCapacityUnavailableException(
        string serverType,
        IReadOnlyList<string> locations,
        IReadOnlyList<string>? attemptedFailures = null,
        Exception? innerException = null)
        : base($"No {serverType.ToUpperInvariant()} capacity is currently available in {FormatLocations(locations)}. No server was created. Try again later.", innerException)
    {
        ServerType = serverType;
        Locations = locations;
        AttemptedFailures = attemptedFailures ?? [];
    }

    public string ServerType { get; }
    public IReadOnlyList<string> Locations { get; }
    public IReadOnlyList<string> AttemptedFailures { get; }

    private static string FormatLocations(IReadOnlyList<string> locations) => locations.Count switch
    {
        0 => "the configured locations",
        1 => locations[0],
        2 => $"{locations[0]} or {locations[1]}",
        _ => $"{string.Join(", ", locations.Take(locations.Count - 1))}, or {locations[^1]}"
    };
}

public interface IRemoteWorkerLifecycle
{
    bool IsManaged(AppSettings settings);
    Task<RemoteWorkerInfo> EnsureReadyAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<RemoteWorkerInfo?> GetAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
