using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Processing;

public sealed record RemoteWorkerInfo(long ServerId, string Name, string Address, string Status, DateTimeOffset? CreatedUtc);

public interface IRemoteWorkerLifecycle
{
    bool IsManaged(AppSettings settings);
    Task<RemoteWorkerInfo> EnsureReadyAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<RemoteWorkerInfo?> GetAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
