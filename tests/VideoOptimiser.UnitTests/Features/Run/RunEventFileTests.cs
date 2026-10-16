using System.Text;
using FluentAssertions;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Infrastructure.Features.Run;

namespace VideoOptimiser.UnitTests.Features.Run;

public sealed class RunEventFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly List<RunEventFile> _files = [];

    public RunEventFileTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "events.db");
    }

    [Fact]
    public void EveryFieldRoundTripsInOrder()
    {
        var log = NewFile();
        log.Begin(_databasePath);
        var jobId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 16, 8, 30, 0, TimeSpan.Zero);
        var full = new RunEvent(
            at, RunEventKind.Progress, jobId, @"C:\Videos\clip.mp4", 3, 2, 1, "line \"quoted\" é", "worker-1", "Keep", "Encoding", "Finished",
            "Detail", "Warning", TimeSpan.FromSeconds(95), true, [new FailedQueueJob(jobId, @"C:\Videos\clip.mp4", "Encode", "boom")]);
        var plain = new RunEvent(at.AddSeconds(1), RunEventKind.RunStarted, Count: 4242, Text: "video-optimiser run");

        log.Append(full);
        log.Append(plain);

        log.Read(_databasePath).Should().Equal(
            [full, plain],
            (expected, actual) => expected.At == actual.At && expected.Kind == actual.Kind && expected.JobId == actual.JobId
                && expected.SourcePath == actual.SourcePath && expected.Count == actual.Count && expected.Ready == actual.Ready
                && expected.Skipped == actual.Skipped && expected.Text == actual.Text && expected.Worker == actual.Worker
                && expected.Fate == actual.Fate && expected.Stage == actual.Stage && expected.RunEnd == actual.RunEnd
                && expected.Progress == actual.Progress && expected.Tone == actual.Tone && expected.TimeLeft == actual.TimeLeft
                && expected.Flag == actual.Flag
                && (expected.Failures ?? []).SequenceEqual(actual.Failures ?? []));
    }

    [Fact]
    public void TheFileIsOneCompactCamelCaseLinePerEventWithWordsForEnumsAndNoNulls()
    {
        var log = NewFile();
        log.Begin(_databasePath);

        log.Append(new RunEvent(new DateTimeOffset(2026, 10, 16, 8, 30, 0, TimeSpan.Zero), RunEventKind.StoppedNow, Count: 2));

        using var stream = new FileStream(RunEventFile.PathFor(_databasePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        reader.ReadToEnd().Should().Be("{\"at\":\"2026-10-16T08:30:00+00:00\",\"kind\":\"StoppedNow\",\"count\":2}\n");
    }

    [Fact]
    public void NoFileReadsAsEmpty()
    {
        NewFile().Read(_databasePath).Should().BeEmpty();
    }

    [Fact]
    public void AppendsBeforeBeginAreDropped()
    {
        var log = NewFile();

        log.Append(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.Summary));

        File.Exists(RunEventFile.PathFor(_databasePath)).Should().BeFalse();
    }

    [Fact]
    public void BeginReplacesTheFileAnEarlierRunLeft()
    {
        var first = NewFile();
        first.Begin(_databasePath);
        first.Append(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.Summary));
        first.Dispose();

        var second = NewFile();
        second.Begin(_databasePath);

        second.Read(_databasePath).Should().BeEmpty();
    }

    [Fact]
    public void BeginReplacesTheFileWhileAnotherInstanceStillHoldsItOpen()
    {
        // The old run's process may still be alive, finishing its worker, when the next run takes the lock.
        var old = NewFile();
        old.Begin(_databasePath);
        old.Append(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.RunStarted, Text: "old"));
        var current = NewFile();

        current.Begin(_databasePath);
        old.Append(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.WorkerDeleting, Text: "old"));
        current.Append(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.RunStarted, Text: "new"));

        current.Read(_databasePath).Select(runEvent => runEvent.Text).Should().Equal("new");
    }

    [Fact]
    public void BeginAgainStartsTheSameInstanceOver()
    {
        var log = NewFile();
        log.Begin(_databasePath);
        log.Append(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.RunStarted, Text: "one"));

        log.Begin(_databasePath);
        log.Append(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.RunStarted, Text: "two"));

        log.Read(_databasePath).Select(runEvent => runEvent.Text).Should().Equal("two");
    }

    [Fact]
    public void ReadWhileAWriterIsOpenSeesEveryFlushedEvent()
    {
        var writer = NewFile();
        writer.Begin(_databasePath);
        var reader = NewFile();

        writer.Append(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.RunStarted, Text: "one"));
        reader.Read(_databasePath).Should().HaveCount(1);
        writer.Append(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.Summary, Text: "two"));

        reader.Read(_databasePath).Select(runEvent => runEvent.Text).Should().Equal("one", "two");
    }

    [Fact]
    public void AFinalLineWithoutItsNewlineIsLeftOut()
    {
        var log = NewFile();
        log.Begin(_databasePath);
        log.Append(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.RunStarted, Text: "whole"));
        // A line that parses on its own but has not been completed: the writer is part-way through it.
        AppendRaw("{\"at\":\"1970-01-01T00:00:00+00:00\",\"kind\":\"Summary\",\"text\":\"half\"}");

        log.Read(_databasePath).Select(runEvent => runEvent.Text).Should().Equal("whole");
    }

    [Fact]
    public void ATornJsonLineIsSkippedAndTheLinesAroundItSurvive()
    {
        var log = NewFile();
        log.Begin(_databasePath);
        log.Append(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.RunStarted, Text: "before"));
        AppendRaw("{\"at\":\"1970-01-01T00:00:00+00:00\",\"ki\n\nnot json\n");
        log.Append(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.Summary, Text: "after"));

        log.Read(_databasePath).Select(runEvent => runEvent.Text).Should().Equal("before", "after");
    }

    [Fact]
    public async Task EventsAppendedFromManyThreadsEachLandOnTheirOwnLine()
    {
        var log = NewFile();
        log.Begin(_databasePath);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++) log.Append(new RunEvent(DateTimeOffset.UnixEpoch, RunEventKind.Progress, Count: thread * 100 + i));
        })));

        log.Read(_databasePath).Select(runEvent => runEvent.Count).Should().BeEquivalentTo(Enumerable.Range(0, 8).SelectMany(thread => Enumerable.Range(0, 50).Select(i => (int?)(thread * 100 + i))));
    }

    private RunEventFile NewFile()
    {
        var file = new RunEventFile();
        _files.Add(file);
        return file;
    }

    /// <summary>Adds bytes to the file as a second writer would, past the end.</summary>
    private void AppendRaw(string text)
    {
        using var stream = new FileStream(RunEventFile.PathFor(_databasePath), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    public void Dispose()
    {
        foreach (var file in _files) file.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
