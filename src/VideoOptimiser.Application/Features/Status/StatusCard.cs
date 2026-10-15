using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Application.Features.Status;

/// <summary>
/// Builds the status card from the queue's open jobs, the lock files, and a Hetzner check that takes no lock. It reaches the
/// queue through <see cref="IQueueDiscovery"/> so the groups here are the ones <c>queue list</c> prints.
/// </summary>
public sealed class StatusCard(IQueueDiscovery discovery, IDatabaseLocks locks, IRemoteWorkerLifecycle lifecycle, TimeProvider time) : IStatusCard
{
    private const int MostNextSteps = 3;

    public async Task<StatusReport> ReadAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var databasePath = settings.Database.Path;
        var open = await discovery.OpenJobsAsync(databasePath, cancellationToken);
        var queue = new StatusCounts(
            Converting: open.Count(entry => entry.Group == OpenJobGroup.ConvertingNow),
            Waiting: open.Count(entry => entry is { Group: OpenJobGroup.Queue, Stopped: null }),
            Stopped: open.Count(entry => entry is { Group: OpenJobGroup.Queue, Stopped: not null }),
            Held: open.Count(entry => entry.Group == OpenJobGroup.Held),
            ReadyToFinalise: open.Count(entry => entry.Group == OpenJobGroup.ReadyToFinalise));

        var run = locks.LiveRun(databasePath) is { } holder
            ? new StatusRun(holder, [.. open.Where(entry => entry.Group == OpenJobGroup.ConvertingNow).Select(entry => new StatusConverting(entry.Job.Id, entry.Job.SourcePath, entry.Job.Status))])
            : null;
        var worker = await ReadWorkerAsync(settings, cancellationToken);
        return new StatusReport(queue, run, worker, ChooseNextSteps(settings, queue, runGoing: run is not null, finaliseGoing: locks.IsFinaliseGoing(databasePath), billing: worker?.Check.Worker is not null));
    }

    /// <summary>
    /// Asked in any <c>processing.mode</c>, because a worker bills whatever mode the config is in now. Only the Hetzner
    /// lifecycle has a worker to ask about; a manual host is the user's own.
    /// </summary>
    private async Task<StatusWorker?> ReadWorkerAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.Processing.RemoteSsh.Lifecycle, RemoteLifecycleModes.Hetzner, StringComparison.OrdinalIgnoreCase)) return null;
        var check = await lifecycle.CheckAsync(settings, settings.Database.Path, cancellationToken);
        // A gone server has no uptime, and a worker Hetzner wasn't asked about may be gone too: its record's age says nothing.
        var up = check is { NotCheckedReason: null, Worker: { CreatedUtc: { } created, Status: not RemoteWorkerStatuses.PrimaryIpRetained } }
            ? time.GetUtcNow() - created
            : (TimeSpan?)null;
        return new StatusWorker(check, up);
    }

    /// <summary>
    /// The commands in the order they matter: what is ready is finalised before anything starts another encode, then the
    /// queue, then what bills, then where more videos come from. A run or a finalise already going is already doing its part.
    /// </summary>
    private static NextStep[] ChooseNextSteps(AppSettings settings, StatusCounts queue, bool runGoing, bool finaliseGoing, bool billing)
    {
        var steps = new List<NextStep>();
        if (queue.ReadyToFinalise > 0 && !finaliseGoing) steps.Add(NextStep.FinaliseReady);
        if (queue.Runnable > 0 && !runGoing) steps.Add(NextStep.Run);
        if (billing && !runGoing) steps.Add(NextStep.DeleteWorker);
        if (queue.Runnable + queue.ReadyToFinalise == 0 && !runGoing)
        {
            if (settings.Watch.Roots.Count > 0) steps.Add(NextStep.AddFromFolders);
            if (settings.Immich.IsConfigured) steps.Add(NextStep.AddFromImmich);
        }
        if (steps.Count == 0) steps.Add(NextStep.ListQueue);
        return steps.Take(MostNextSteps).ToArray();
    }
}
