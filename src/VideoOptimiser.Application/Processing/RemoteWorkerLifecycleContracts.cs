using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Processing;

/// <param name="ServerType">Empty when Hetzner did not report it for a recovered worker.</param>
/// <param name="Location">Empty when unknown, as for <paramref name="ServerType"/>.</param>
public sealed record RemoteWorkerInfo(long ServerId, string Name, string Address, string Status, DateTimeOffset? CreatedUtc, string ServerType, string Location);

/// <summary>Why one configured server type produced no server.</summary>
/// <param name="LocationsTried">The configured locations checked for this type; empty when the type itself was unsuitable.</param>
public sealed record RemoteWorkerTypeOutcome(string ServerType, IReadOnlyList<string> LocationsTried, string Reason);

/// <summary>
/// Indicates that no configured server type could be created in any configured location,
/// either at preflight or after placement attempts. No server is left running.
/// </summary>
public sealed class RemoteWorkerCapacityUnavailableException : InvalidOperationException
{
    public RemoteWorkerCapacityUnavailableException(
        IReadOnlyList<RemoteWorkerTypeOutcome> outcomes,
        IReadOnlyList<string>? attemptedFailures = null,
        Exception? innerException = null)
        : base(FormatMessage(outcomes), innerException)
    {
        Outcomes = outcomes;
        AttemptedFailures = attemptedFailures ?? [];
    }

    public IReadOnlyList<RemoteWorkerTypeOutcome> Outcomes { get; }

    /// <summary>Each failed create request, as "type in location (reason)".</summary>
    public IReadOnlyList<string> AttemptedFailures { get; }

    private static string FormatMessage(IReadOnlyList<RemoteWorkerTypeOutcome> outcomes)
    {
        var lines = outcomes.Select(outcome => outcome.LocationsTried.Count == 0
            ? $"  {outcome.ServerType}: {outcome.Reason}"
            : $"  {outcome.ServerType} in {string.Join(", ", outcome.LocationsTried)}: {outcome.Reason}");
        return "No Hetzner server could be created, so none is running. Tried:" + Environment.NewLine +
               string.Join(Environment.NewLine, lines) + Environment.NewLine +
               "Try again later, or add server types or locations.";
    }
}

public interface IRemoteWorkerLifecycle
{
    bool IsManaged(AppSettings settings);
    Task<RemoteWorkerInfo> EnsureReadyAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<RemoteWorkerInfo?> GetAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// The worker in the local state file, without a Hetzner API call or the lifecycle lock, so a run can say where it will
    /// encode before anything else. Its status is <c>recorded</c>: the server may have been deleted outside VideoOptimiser.
    /// </summary>
    Task<RemoteWorkerInfo?> ReadRecordedAsync(string databasePath, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
