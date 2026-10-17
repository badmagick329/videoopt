using System.Collections.Immutable;
using VideoOptimiser.Application.Features.Cost;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli.Features.Tui;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.UnitTests.Features.Tui;

/// <summary>Models, snapshots, events and keys for the TUI tests; the file names are placeholders.</summary>
internal static class TuiTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 16, 14, 3, 20, TimeSpan.Zero);

    public const long Mib = 1024 * 1024;

    /// <summary>A job ID the test can name: the same number is the same video in every snapshot.</summary>
    public static Guid Id(int number) => new(number, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    public static Video Queued(int number, string? name = null, long size = 400 * Mib) =>
        new(Id(number), name ?? $"clip-{number:000}.mp4", VideoGroup.Queue, size, JobStatus.Queued);

    public static Video Held(int number, long size = 100 * Mib) =>
        new(Id(number), $"clip-{number:000}.mp4", VideoGroup.Held, size, JobStatus.Queued);

    public static Video Converting(int number, JobStatus status = JobStatus.Encoding, bool remote = false, long size = 1126 * Mib) =>
        new(Id(number), $"clip-{number:000}.mp4", VideoGroup.ConvertingNow, size, status, Remote: remote);

    public static Video Ready(int number, long size = 800 * Mib, long av1 = 300 * Mib, decimal saved = 62.5m) =>
        new(Id(number), $"clip-{number:000}.mp4", VideoGroup.Ready, size, JobStatus.ReadyToFinalize, Av1Bytes: av1, PercentSaved: saved);

    public static Video Failed(int number, string note = "encoding failed: exit code 1") =>
        new(Id(number), $"clip-{number:000}.mp4", VideoGroup.Failed, 220 * Mib, JobStatus.Failed, Note: note);

    public static TuiModel Model(params Video[] open) => TuiModel.Initial(Now, 140, 40, "jobs.db") with
    {
        Loaded = true,
        Open = [.. open],
        QueueSelection = null,
    };

    public static TuiModel WithFailed(this TuiModel model, params Video[] failed) => model with { Failed = [.. failed] };

    public static TuiSnapshot Snapshot(
        IEnumerable<Video>? open = null,
        IEnumerable<Video>? failed = null,
        RunSection? run = null,
        WorkerSection? worker = null,
        IReadOnlyList<RunEvent>? events = null,
        params string[] problems) =>
        new(open is null ? null : [.. open], failed is null ? null : [.. failed], run, worker, events, problems);

    /// <summary>A snapshot of the whole queue with the run, worker and event sections reporting nothing.</summary>
    public static TuiSnapshot Quiet(IEnumerable<Video> open, IEnumerable<Video>? failed = null) =>
        Snapshot(open, failed ?? [], new RunSection(null, null), new WorkerSection(false, "manual", null), []);

    public static TuiStep Update(TuiModel model, TuiMsg msg) => TuiUpdate.Update(model, msg);

    public static TuiModel After(TuiModel model, params TuiMsg[] messages) =>
        messages.Aggregate(model, (current, msg) => TuiUpdate.Update(current, msg).Model);

    public static TuiMsg Snap(TuiSnapshot snapshot) => new SnapshotMsg(snapshot);

    public static TuiMsg Tick(TimeSpan after, int width = 140, int height = 40) => new TickMsg(Now + after, width, height);

    public static TuiMsg Press(ConsoleKey key, char character = '\0', ConsoleModifiers modifiers = 0) =>
        new KeyMsg(new ConsoleKeyInfo(character, key, modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt), modifiers.HasFlag(ConsoleModifiers.Control)));

    public static TuiMsg Letter(char letter) => Press((ConsoleKey)char.ToUpperInvariant(letter), letter);

    public static RunEvent Started(int processId = 4242, int secondsAfterNow = 0, string holder = LockHolders.BackgroundRun, string? fate = "Delete") =>
        new(Now.AddSeconds(secondsAfterNow), RunEventKind.RunStarted, Text: holder, Count: processId, Fate: fate);

    public static RunEvent Event(RunEventKind kind, int secondsAfterNow = 1, string? path = null, int? count = null, string? text = null, bool? flag = null) =>
        new(Now.AddSeconds(secondsAfterNow), kind, SourcePath: path, Count: count, Text: text, Flag: flag);

    public static RunEvent Progress(JobProgressKind kind, string text, JobTone tone = JobTone.None, int secondsAfterNow = 1, int? percent = null) =>
        new(Now.AddSeconds(secondsAfterNow), RunEventKind.Progress, Text: text, Progress: kind.ToString(), Tone: tone.ToString(), Percent: percent);

    // ---- the cost page ---------------------------------------------------------------------

    public static TuiMsg Dollar => Press(ConsoleKey.D4, '$', ConsoleModifiers.Shift);

    public const long GB = 1_000_000_000;

    public static CostVideo CostVideo(string name, int minutes, decimal eur, CostVideoState state, JobStatus status, long saved = 0, decimal? perTerabyte = null) =>
        new(Guid.NewGuid(), name, TimeSpan.FromMinutes(minutes), eur, saved, state, status, perTerabyte, PriceMissing: false);

    public static CostRun CostRun(int number, int startedMinutesAgo, int minutes, decimal eur, long freed, long waiting, decimal? perTerabyte, params CostVideo[] videos) =>
        new(Id(number), Now.AddMinutes(-startedMinutesAgo), Now.AddMinutes(minutes - startedMinutesAgo), 42, "video-optimiser-1", 0.0127m,
            TimeSpan.FromMinutes(minutes), eur, freed, waiting, perTerabyte, videos);

    /// <summary>Two runs on a cx43 at EUR 0.0127 an hour, the newest with a video of each kind; the figures are the report's, not recomputed here.</summary>
    public static CostReport ACostReport(bool stillBilling = false) => new(
        new CostLifetime(0.0381m, 47 * GB / 10, 8 * GB / 10, 8.11m, 2, stillBilling, EndEstimated: false, PriceMissing: false),
        [
            CostRun(2, 120, 90, 0.019m, 32 * GB / 10, 8 * GB / 10, 5.95m,
                CostVideo("holiday.mp4", 40, 0.0085m, CostVideoState.Freed, JobStatus.Completed, 2 * GB, 4.23m),
                CostVideo("garden.mp4", 25, 0.0053m, CostVideoState.Freed, JobStatus.Completed, 12 * GB / 10, 4.41m),
                CostVideo("party.mov", 12, 0.0025m, CostVideoState.NothingSaved, JobStatus.Failed),
                CostVideo("beach.mp4", 8, 0.0017m, CostVideoState.WaitingForReview, JobStatus.ReadyToFinalize, 8 * GB / 10)),
            CostRun(1, 1500, 60, 0.0127m, 15 * GB / 10, 0, 8.47m,
                CostVideo("birthday.mp4", 45, 0.0095m, CostVideoState.Freed, JobStatus.Completed, 15 * GB / 10, 6.33m))
        ]);

    public static RemoteWorkerInfo AWorker(long id = 42, string name = "video-optimiser-1", string status = RemoteWorkerStatuses.Recorded, DateTimeOffset? created = null) =>
        new(id, name, "203.0.113.10", status, created ?? Now - TimeSpan.FromMinutes(72), "cx43", "hel1");
}
