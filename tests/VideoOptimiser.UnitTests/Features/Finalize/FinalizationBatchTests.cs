using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.UnitTests.Features.Tui;

namespace VideoOptimiser.UnitTests.Features.Finalize;

/// <summary>
/// The batch loop below its front ends, with the finalisation faked and the real locks over a temporary database. How each
/// front end words the outcome, and Ctrl+C's exit code, are pinned through the commands (<c>FinalizeCommandsTests</c>,
/// <c>TuiActionTests</c>).
/// </summary>
public sealed class FinalizationBatchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly RecordingFinalizer _finalizer = new();
    private readonly ReadFails _jobs = new(new SqliteJobRepository(new SqliteDatabaseInitializer()));
    private readonly RecordingObserver _observer = new();
    private readonly AppSettings _settings = new();
    private readonly FinalizationBatch _batch;

    public FinalizationBatchTests()
    {
        Directory.CreateDirectory(_directory);
        _settings.Database.Path = Path.Combine(_directory, "jobs.db");
        _batch = new FinalizationBatch(_jobs, _finalizer, new DatabaseLocks());
    }

    /// <summary>
    /// A stop asked during a video reaches neither that video's finalisation nor its end: it finishes, and no other starts.
    /// A token linked to the stop would cut an Immich upload or a file move off partway.
    /// </summary>
    [Fact]
    public async Task AStopAskedDuringAVideoNeverReachesItsFinalisationAndStartsNoOther()
    {
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        using var stop = new CancellationTokenSource();
        _finalizer.During = _ => stop.Cancel();

        var outcome = await _batch.RunAsync(ids, _settings, "finalize", _observer, stop.Token, default);

        _finalizer.Finalised.Should().Equal(ids[0]);
        _finalizer.Tokens.Single().IsCancellationRequested.Should().BeFalse();
        _observer.Events.Should().Equal($"started {ids[0]}", $"ended {ids[0]} finalised");
        outcome.Should().Be(new FinalizationBatchTally(3, 1, 0)).And.BeOfType<FinalizationBatchTally>().Which.Left.Should().Be(2);
    }

    /// <summary>
    /// Whatever a video throws short of the caller's own cancellation, it is that video's failure and the next one goes: a
    /// failed read ends it without a start, and a cancellation from elsewhere, such as an HTTP timeout, ends a started one.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AVideoThatThrowsIsCountedAsFailedAndTheNextOneStillGoes(bool readFails)
    {
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        if (readFails) _jobs.Failing = ids[0];
        else _finalizer.Failures[ids[0]] = new TaskCanceledException("The request timed out.");

        var outcome = await _batch.RunAsync(ids, _settings, "finalize", _observer, default, default);

        outcome.Should().Be(new FinalizationBatchTally(2, 1, 1));
        _finalizer.Finalised.Should().Equal(readFails ? [ids[1]] : ids);
        string[] first = readFails ? [$"ended {ids[0]} {nameof(IOException)}"] : [$"started {ids[0]}", $"ended {ids[0]} {nameof(TaskCanceledException)}"];
        _observer.Events.Should().Equal([.. first, $"started {ids[1]}", $"ended {ids[1]} finalised"]);
    }

    /// <summary>The front end words the refusal, so the batch hands back who holds the lock instead of a sentence.</summary>
    [Fact]
    public async Task ARefusedLockFinalisesNothingAndHandsBackItsHolder()
    {
        using var held = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(_settings.Database.Path), LockHolders.QueueReject, out _);

        var outcome = await _batch.RunAsync([Guid.NewGuid()], _settings, "finalize", _observer, default, default);

        var holder = outcome.Should().BeOfType<FinalizationBatchRefused>().Subject.Holder!;
        holder.Command.Should().Be(LockHolders.QueueReject);
        holder.ProcessId.Should().Be(Environment.ProcessId);
        _finalizer.Finalised.Should().BeEmpty();
        _observer.Events.Should().BeEmpty();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>The real repository, with one job's read failing as a locked or corrupt database would.</summary>
    private sealed class ReadFails(IJobRepository inner) : DelegatingJobRepository(inner)
    {
        public Guid? Failing { get; set; }

        public override Task<JobRecord?> GetAsync(string databasePath, Guid id, CancellationToken cancellationToken = default) =>
            id == Failing ? throw new IOException("The database is locked.") : base.GetAsync(databasePath, id, cancellationToken);
    }

    private sealed class RecordingObserver : IFinalizationBatchObserver
    {
        public List<string> Events { get; } = [];

        public void VideoStarted(Guid id, JobRecord? job) => Events.Add($"started {id}");

        public void VideoEnded(FinalizationBatchVideo video) =>
            Events.Add($"ended {video.Id} {(video.Succeeded ? "finalised" : video.Error?.GetType().Name ?? "answered")}");
    }
}
