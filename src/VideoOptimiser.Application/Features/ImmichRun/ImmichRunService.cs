using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Immich;

namespace VideoOptimiser.Application.Features.ImmichRun;

/// <summary>
/// What <c>queue add immich</c> does, and <c>immich run</c> before it converts. The guard comes first, so a re-uploaded
/// original is back in the trash even when discovery later fails. With Immich's trash off the run stops before anything: the
/// guard could not trash safely, and nothing it converted could be finalised.
/// </summary>
public sealed class ImmichRunService(IImmichClientFactory clients, IImmichReuploadGuard guard, IImmichDiscoveryService discovery) : IImmichRunService
{
    public async Task<ImmichQueueAddition> AddToQueueAsync(string databasePath, AppSettings settings, int? limit, IProgress<ScanProgress>? guardProgress = null, IProgress<ImmichProgress>? discoveryProgress = null, CancellationToken cancellationToken = default)
    {
        await ImmichTrash.RequireOnAsync(clients.Create(settings.Immich), cancellationToken);
        var guarded = await guard.CheckAsync(databasePath, settings, guardProgress, cancellationToken);
        var discovered = await discovery.DiscoverAsync(databasePath, settings, limit, discoveryProgress, cancellationToken);
        return new ImmichQueueAddition(guarded, discovered);
    }
}
