using System.Collections.Immutable;
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

    public static RemoteWorkerInfo AWorker(long id = 42, string name = "video-optimiser-1", string status = RemoteWorkerStatuses.Recorded, DateTimeOffset? created = null) =>
        new(id, name, "203.0.113.10", status, created ?? Now - TimeSpan.FromMinutes(72), "cx43", "hel1");
}
