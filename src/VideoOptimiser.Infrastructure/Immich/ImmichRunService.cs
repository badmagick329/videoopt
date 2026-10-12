using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Scanning;

namespace VideoOptimiser.Infrastructure.Immich;

/// <summary>
/// The steps of <c>immich run</c> before processing. The guard comes first, so a re-uploaded original is back in the trash
/// even when discovery or processing later fails.
/// </summary>
public sealed class ImmichRunService(IImmichReuploadGuard guard, IImmichDiscoveryService discovery, IQueueService queue) : IImmichRunService
{
    public async Task<ImmichRunPreparation> PrepareAsync(string databasePath, AppSettings settings, bool first, IProgress<ScanProgress>? guardProgress = null, IProgress<ScanProgress>? discoveryProgress = null, CancellationToken cancellationToken = default)
    {
        var guarded = await guard.CheckAsync(databasePath, settings, guardProgress, cancellationToken);
        var discovered = await discovery.DiscoverAsync(databasePath, settings, first, discoveryProgress, cancellationToken);
        var prepared = await queue.PrepareAsync(databasePath, settings, JobSourceKind.Immich, cancellationToken);
        // --first is the cautious first round: one job is processed and then finalised, however many were already open.
        if (first && prepared.RunnableJobs.Count > 1) prepared = prepared with { RunnableJobs = prepared.RunnableJobs.Take(1).ToArray() };
        return new ImmichRunPreparation(guarded, discovered, prepared);
    }
}
