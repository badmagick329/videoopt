using System.Collections.Immutable;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// Every read the screen needs, behind one class that takes the Application ports, so a test drives it with fakes or a temporary
/// database. A section whose read throws (the database locked for a moment, the event file in use) comes back null with a line
/// in <see cref="TuiSnapshot.Problems"/>, and the model keeps what it had: a hiccup must not blank the queue or end the TUI.
/// </summary>
internal sealed class TuiSnapshotReader(
    IQueueDiscovery discovery,
    IJobRepository jobs,
    IDatabaseLocks locks,
    IRunRequestStore requests,
    IRemoteWorkerLifecycle lifecycle,
    IRunEventLog eventLog)
{
    /// <summary>The Failed group is the latest failures, not the whole history.</summary>
    public const int MostFailed = 20;

    /// <summary>Reads everything but Hetzner (<see cref="CheckWorkerAsync"/>): local files and the database, each cheap enough to do every second.</summary>
    public async Task<TuiSnapshot> ReadAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var database = settings.Database.Path;
        var problems = new List<string>();

        IReadOnlyList<OpenJobEntry>? entries = null;
        ImmutableList<Video>? open = null;
        try
        {
            entries = await discovery.OpenJobsAsync(database, cancellationToken);
            open = [.. entries.Select(ToVideo)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            problems.Add(Problem("the queue", exception));
        }

        // The failed group needs the open jobs: a failed video that a retry queued again is not failed any more.
        ImmutableList<Video>? failed = null;
        if (entries is not null)
        {
            try
            {
                failed = FailedVideos(await jobs.ListAsync(database, terminal: true, cancellationToken), entries.Select(entry => entry.Job));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                problems.Add(Problem("the failed videos", exception));
            }
        }

        RunSection? run = null;
        try
        {
            var holder = locks.LiveRun(database);
            run = new RunSection(holder, holder is null ? null : await requests.ReadAsync(database, cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            problems.Add(Problem("the run", exception));
        }

        WorkerSection? worker = null;
        var configured = settings.Processing.RemoteSsh.Lifecycle;
        try
        {
            var managed = string.Equals(configured, RemoteLifecycleModes.Hetzner, StringComparison.OrdinalIgnoreCase);
            // Local and cheap: the state file, never Hetzner.
            worker = new WorkerSection(managed, configured, managed ? await lifecycle.ReadRecordedAsync(database, cancellationToken) : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            problems.Add(Problem("the worker", exception));
        }

        IReadOnlyList<RunEvent>? events = null;
        try
        {
            events = eventLog.Read(database);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            problems.Add(Problem("the run's events", exception));
        }

        return new TuiSnapshot(open, failed, run, worker, events, problems);
    }

    /// <summary>
    /// Asks Hetzner about the managed worker (up to 15 seconds). It never throws for a failure: that is the reason the answer
    /// couldn't be had, which the worker pane shows.
    /// </summary>
    public async Task<WorkerCheck> CheckWorkerAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            return await lifecycle.CheckAsync(settings, settings.Database.Path, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new WorkerCheck(null, null, OneLine(exception));
        }
    }

    /// <summary>
    /// The latest failures: a failed job is shown while no later job exists for the same original, open or ended, because a
    /// retry, a new discovery or a conversion after it replaces it, and until <c>queue dismiss</c> hides it. Newest first, at
    /// most <see cref="MostFailed"/>.
    /// </summary>
    internal static ImmutableList<Video> FailedVideos(IEnumerable<JobRecord> ended, IEnumerable<JobRecord> open)
    {
        var all = ended.Concat(open).ToList();
        var newestBySource = all
            .GroupBy(job => job.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(job => job.CreatedUtc), StringComparer.OrdinalIgnoreCase);
        return
        [
            .. ended
                .Where(job => job.Status == JobStatus.Failed && job.DismissedUtc is null && job.CreatedUtc >= newestBySource[job.SourcePath])
                .OrderByDescending(job => job.CompletedUtc ?? job.UpdatedUtc)
                .Take(MostFailed)
                .Select(FailedVideo)
        ];
    }

    private static Video FailedVideo(JobRecord job) => new(
        job.Id, Path.GetFileName(job.SourcePath), VideoGroup.Failed, SizeOf(job), job.Status,
        Remote: IsRemote(job),
        Note: RunEventWords.Clean(FirstLine(FailureWords.Describe(job.FailureCategory, job.FailureMessage, sentence: false) ?? "failed")));

    private static Video ToVideo(OpenJobEntry entry)
    {
        var job = entry.Job;
        var group = entry.Group switch
        {
            OpenJobGroup.ConvertingNow => VideoGroup.ConvertingNow,
            OpenJobGroup.Held => VideoGroup.Held,
            OpenJobGroup.ReadyToFinalise => VideoGroup.Ready,
            _ => VideoGroup.Queue
        };
        var note = entry.State switch
        {
            OpenJobState.LastFinaliseFailed => "last finalising failed",
            OpenJobState.StoppedWhileFinalising => "finalising stopped",
            OpenJobState.Finalising => "finalising",
            _ => null
        };
        return new Video(job.Id, Path.GetFileName(job.SourcePath), group, SizeOf(job), job.Status,
            Remote: IsRemote(job), Stopped: entry.Stopped is not null,
            Av1Bytes: group == VideoGroup.Ready ? job.OutputSizeBytes : null,
            PercentSaved: group == VideoGroup.Ready ? job.PercentageSaved : null,
            Note: note, Immich: job.SourceKind == JobSourceKind.Immich,
            Warnings: group == VideoGroup.Ready ? job.ValidationWarnings : null);
    }

    private static long? SizeOf(JobRecord job) => job.SourceSizeBytes ?? job.VideoFacts?.SizeBytes;

    /// <summary>
    /// What finalising a ready job saves, 0 when a size is unknown. It is <see cref="Video.SavedBytes"/> over the sizes
    /// <see cref="ToVideo"/> takes, so a finalise batch's running total adds up to the <c>would save</c> Review showed before it.
    /// </summary>
    internal static long SavedBytesOf(JobRecord job) => SizeOf(job) is { } size && job.OutputSizeBytes is { } av1 ? size - av1 : 0;

    private static bool IsRemote(JobRecord job) => string.Equals(job.ExecutionMode, ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase);

    private static string Problem(string what, Exception exception)
    {
        var why = OneLine(exception);
        return $"Couldn't read {what}: {why}{(why.EndsWith('.') ? string.Empty : ".")}";
    }

    private static string OneLine(Exception exception) => RunEventWords.Clean(FirstLine(exception.GetBaseException().Message));

    private static string FirstLine(string text) => text.Split('\n', 2)[0].TrimEnd('\r');
}
