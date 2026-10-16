using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Status;

/// <summary>What the open jobs add up to, in the groups <c>queue list</c> shows them under.</summary>
/// <param name="Waiting">Waiting videos a run takes; <paramref name="Stopped"/> are the ones a stop left, which it also takes.</param>
/// <param name="ReadyToFinalise">Ready, last finalise stopped, being finalised or stopped while it was: finalize's work.</param>
/// <param name="ToCheck">The ready ones with validation warnings, which <c>finalize --ready</c> leaves for the person to finalise one by one.</param>
public sealed record StatusCounts(int Converting, int Waiting, int Stopped, int Held, int ReadyToFinalise, int ToCheck)
{
    /// <summary>What a run would take.</summary>
    public int Runnable => Waiting + Stopped;

    /// <summary>What <c>finalize --ready</c> would take.</summary>
    public int BulkFinalisable => ReadyToFinalise - ToCheck;
}

/// <summary>A video the run going now is converting.</summary>
public sealed record StatusConverting(Guid Id, string SourcePath, JobStatus Status);

/// <param name="Holder">The run as its lock file records it.</param>
public sealed record StatusRun(LiveRunHolder Holder, IReadOnlyList<StatusConverting> Converting);

/// <param name="Check">What Hetzner said, or the recorded worker when it couldn't be asked (<see cref="WorkerCheck.NotCheckedReason"/>).</param>
/// <param name="Uptime">How long the server has been up; null when Hetzner wasn't asked, the server is gone, or no creation time is known.</param>
public sealed record StatusWorker(WorkerCheck Check, TimeSpan? Uptime);

/// <summary>The commands worth running now, in the order they matter. The CLI words them.</summary>
public enum NextStep
{
    /// <summary><c>finalize --ready</c>.</summary>
    FinaliseReady,
    /// <summary><c>queue list</c>, to read the warnings of the ready videos bulk finalise leaves out.</summary>
    CheckWarned,
    /// <summary><c>run</c>.</summary>
    Run,
    /// <summary><c>worker delete</c>: something bills.</summary>
    DeleteWorker,
    /// <summary><c>queue add</c>.</summary>
    AddFromFolders,
    /// <summary><c>queue add immich</c>.</summary>
    AddFromImmich,
    /// <summary><c>queue list</c>: nothing else is worth doing.</summary>
    ListQueue
}

/// <param name="Run">Null when no run is going.</param>
/// <param name="Worker">Null when the lifecycle isn't Hetzner: then there is no worker to ask about.</param>
/// <param name="Next">At most three.</param>
public sealed record StatusReport(StatusCounts Queue, StatusRun? Run, StatusWorker? Worker, IReadOnlyList<NextStep> Next);

/// <summary>
/// <c>status</c>: the queue, the run, the worker and what to do next on one screen. It reads and never waits on a lock, so it
/// answers while a run is busy creating a worker.
/// </summary>
public interface IStatusCard
{
    Task<StatusReport> ReadAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
