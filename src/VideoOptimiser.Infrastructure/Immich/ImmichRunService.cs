using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Infrastructure.Immich;

/// <summary>
/// The steps of <c>immich run</c> before processing. The guard comes first, so a re-uploaded original is back in the trash
/// even when discovery or processing later fails. With Immich's trash off the run stops before anything: the guard could
/// not trash safely, and nothing it converted could be finalised.
/// </summary>
public sealed class ImmichRunService(IImmichClientFactory clients, IImmichReuploadGuard guard, IImmichDiscoveryService discovery, IQueueService queue) : IImmichRunService
{
    public async Task<ImmichRunPreparation> PrepareAsync(string databasePath, AppSettings settings, int? limit, IProgress<ScanProgress>? guardProgress = null, IProgress<ImmichDiscoveryProgress>? discoveryProgress = null, bool modeOverridden = false, CancellationToken cancellationToken = default)
    {
        await ImmichTrash.RequireOnAsync(clients.Create(settings.Immich), cancellationToken);
        var guarded = await guard.CheckAsync(databasePath, settings, guardProgress, cancellationToken);
        var discovered = await discovery.DiscoverAsync(databasePath, settings, limit, discoveryProgress, cancellationToken);
        var prepared = await queue.PrepareAsync(databasePath, settings, JobSourceKind.Immich, modeOverridden, cancellationToken);
        // A limit bounds the round to review and finalise, however many jobs were already open. The set stays fixed for the
        // run: a video queued, released or moved next meanwhile waits for the next round.
        if (prepared.RunnableJobs.Count > limit) prepared = prepared with { RunnableJobs = prepared.RunnableJobs.Take(limit.Value).ToArray() };
        return new ImmichRunPreparation(guarded, discovered, prepared with { FollowQueue = false });
    }
}
