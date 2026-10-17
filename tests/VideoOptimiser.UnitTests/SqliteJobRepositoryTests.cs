using FluentAssertions;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests;

public sealed class SqliteJobRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    public SqliteJobRepositoryTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "jobs.db");
    }

    /// <summary>The only tests of <c>JobStateTransitions</c>: a job cannot jump to Completed, only a CRF search ends Skipped, and only a ready job ends Rejected, which nothing leaves.</summary>
    [Theory]
    [InlineData(JobStatus.Queued, JobStatus.Completed)]
    [InlineData(JobStatus.Encoding, JobStatus.Skipped)]
    [InlineData(JobStatus.Queued, JobStatus.Rejected)]
    [InlineData(JobStatus.Validating, JobStatus.Rejected)]
    [InlineData(JobStatus.Interrupted, JobStatus.Rejected)]
    [InlineData(JobStatus.Rejected, JobStatus.Queued)]
    [InlineData(JobStatus.Rejected, JobStatus.ReadyToFinalize)]
    public async Task RejectsInvalidStateTransitions(JobStatus from, JobStatus to)
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(from));
        job.Status = to;

        var action = async () => await _repository.UpdateAsync(_databasePath, job);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>Guards the per-open schema check: a repository that remembered a path as initialised would find no tables in a file replaced under it.</summary>
    [Fact]
    public async Task CreatesTheSchemaAgainInADatabaseDeletedAndReplacedAtTheSamePath()
    {
        await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        foreach (var file in Directory.GetFiles(_directory)) File.Delete(file);

        var created = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued, "C:\\Videos\\other.mp4"));

        (await _repository.GetAsync(_databasePath, created.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task FindsTheOpenJobForAnImmichAssetOnly()
    {
        await _repository.CreateAsync(_databasePath, NewImmichJob("asset-1", JobStatus.Completed));
        await _repository.CreateAsync(_databasePath, NewImmichJob("asset-1", JobStatus.Failed));
        await _repository.CreateAsync(_databasePath, NewImmichJob("asset-2", JobStatus.Encoding));

        (await _repository.FindOpenByImmichAssetAsync(_databasePath, "asset-1")).Should().BeNull();

        var open = await _repository.CreateAsync(_databasePath, NewImmichJob("asset-1", JobStatus.ReadyToFinalize));
        (await _repository.FindOpenByImmichAssetAsync(_databasePath, "asset-1"))!.Id.Should().Be(open.Id);
    }

    [Fact]
    public async Task ListsOnlyCompletedImmichJobsOldestFirst()
    {
        var newer = NewImmichJob("asset-newer", JobStatus.Completed);
        newer.CompletedUtc = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        var older = NewImmichJob("asset-older", JobStatus.Completed);
        older.CompletedUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await _repository.CreateAsync(_databasePath, newer);
        await _repository.CreateAsync(_databasePath, older);
        await _repository.CreateAsync(_databasePath, NewImmichJob("asset-failed", JobStatus.Failed));
        await _repository.CreateAsync(_databasePath, NewImmichJob("asset-open", JobStatus.ReadyToFinalize));
        await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Completed));

        var completed = await _repository.ListCompletedImmichAsync(_databasePath);

        completed.Select(job => job.ImmichAssetId).Should().Equal("asset-older", "asset-newer");
    }

    /// <summary>Putting an asset again replaces the whole record, its facts included, and a record without facts clears them.</summary>
    [Fact]
    public async Task AReplacedImmichSkipTakesTheNewRecordAndTheNewVideoFactsOrNone()
    {
        var recorded = new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);
        var facts = new VideoFacts("h264", 1920, 1080, 22_000_000, 31.5, 86_000_000);
        var newer = new VideoFacts("hevc", 3840, 2160, 40_000_000, 62, 300_000_000);
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-1", "checksum-a", "rules-a", "no rule for h264", recorded, facts));
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-1", "checksum-d", "rules-b", "no rule for hevc", recorded.AddDays(1), newer));
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-2", "checksum-b", "rules-a", "no rule for h264", recorded, facts));
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-2", "checksum-c", "rules-b", "too small or not a listed file type", recorded));

        var skips = await _repository.ListImmichSkipsAsync(_databasePath);

        skips.Should().HaveCount(2);
        skips["asset-1"].Should().Be(new ImmichSkipRecord("asset-1", "checksum-d", "rules-b", "no rule for hevc", recorded.AddDays(1), newer));
        skips["asset-2"].Should().Be(new ImmichSkipRecord("asset-2", "checksum-c", "rules-b", "too small or not a listed file type", recorded));
    }

    [Fact]
    public async Task AJobKeepsItsVideoFactsThroughCreateAndUpdate()
    {
        var job = NewJob(JobStatus.Queued);
        job.VideoFacts = new VideoFacts("hevc", 3840, 2160, null, 12.25, 52_428_800);
        await _repository.CreateAsync(_databasePath, job);
        (await _repository.GetAsync(_databasePath, job.Id))!.VideoFacts.Should().Be(job.VideoFacts);

        job.VideoFacts = new VideoFacts("h264", 1920, 1080, 18_000_000, 40, 90_000_000);
        job.Status = JobStatus.CrfSearching;
        await _repository.UpdateAsync(_databasePath, job);

        (await _repository.GetAsync(_databasePath, job.Id))!.VideoFacts.Should().Be(job.VideoFacts);
    }

    [Fact]
    public async Task ARowWithAnUnknownSourceKindIsRefusedRatherThanReadAsAFile()
    {
        // A newer version may have written the kind; reading it as "file" would treat an Immich staged copy as a user's original.
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        await using (var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET source_kind = 'future-kind' WHERE id = $id;";
            command.Parameters.AddWithValue("$id", job.Id.ToString("N"));
            await command.ExecuteNonQueryAsync();
        }

        var action = async () => await _repository.GetAsync(_databasePath, job.Id);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unknown source kind 'future-kind'*");
    }

    /// <summary>
    /// A run holds its copy of a job while it checks the original; a drop from another terminal in that window must win, not
    /// be undone by the run's next write (VOPT-54 step 5).
    /// </summary>
    [Fact]
    public async Task AWriteFromACopyReadBeforeAQueueVerbFailsAndLeavesTheVerbsChange()
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        var runCopy = (await _repository.GetAsync(_databasePath, job.Id))!;
        var verbCopy = (await _repository.GetAsync(_databasePath, job.Id))!;
        (await _repository.SetHeldAsync(_databasePath, verbCopy, held: true)).Should().BeTrue();

        runCopy.Status = JobStatus.Staging;
        var write = async () => await _repository.UpdateAsync(_databasePath, runCopy);

        await write.Should().ThrowAsync<JobChangedException>();
        var stored = (await _repository.GetAsync(_databasePath, job.Id))!;
        stored.Status.Should().Be(JobStatus.Queued);
        stored.Held.Should().BeTrue();
    }

    /// <summary>Nothing writes a held job, so a hold can never be half applied: a stopped job keeps its resume point.</summary>
    [Fact]
    public async Task AHeldJobRefusesEveryWriteButTheQueueVerbs()
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        (await _repository.SetHeldAsync(_databasePath, job, held: true)).Should().BeTrue();

        job.Status = JobStatus.CrfSearching;
        var write = async () => await _repository.UpdateAsync(_databasePath, job);

        await write.Should().ThrowAsync<JobChangedException>();
        job.Status = JobStatus.Queued;
        (await _repository.SetHeldAsync(_databasePath, job, held: false)).Should().BeTrue();
        job.Status = JobStatus.CrfSearching;
        await _repository.UpdateAsync(_databasePath, job);
        (await _repository.GetAsync(_databasePath, job.Id))!.Status.Should().Be(JobStatus.CrfSearching);
    }

    [Fact]
    public async Task AStaleCopyOfADroppedJobFailsAsChangedNotAsAnIllegalTransition()
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        var runCopy = (await _repository.GetAsync(_databasePath, job.Id))!;
        (await _repository.DropAsync(_databasePath, job)).Should().BeTrue();

        runCopy.Status = JobStatus.Failed;
        var write = async () => await _repository.UpdateAsync(_databasePath, runCopy);

        await write.Should().ThrowAsync<JobChangedException>();
        (await _repository.GetAsync(_databasePath, job.Id))!.Status.Should().Be(JobStatus.Cancelled);
    }

    /// <summary>
    /// R9: crash recovery interrupts only the jobs that were in flight, and it is a write too, so a copy read before it
    /// cannot undo it.
    /// </summary>
    [Fact]
    public async Task AWriteFromACopyReadBeforeCrashRecoveryIsRefused()
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        job.Status = JobStatus.Encoding;
        await _repository.UpdateAsync(_databasePath, job);
        var ready = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.ReadyToFinalize, @"C:\Videos\other.mp4"));
        var staleCopy = (await _repository.GetAsync(_databasePath, job.Id))!;
        await _repository.MarkActiveJobsInterruptedAsync(_databasePath, includeFinalizing: true);

        staleCopy.Status = JobStatus.Validating;
        var write = async () => await _repository.UpdateAsync(_databasePath, staleCopy);

        await write.Should().ThrowAsync<JobChangedException>();
        (await _repository.GetAsync(_databasePath, job.Id))!.Status.Should().Be(JobStatus.Interrupted);
        (await _repository.GetAsync(_databasePath, ready.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    /// <summary>A verb that read the job before a run started it refuses rather than dropping a video mid-conversion.</summary>
    [Fact]
    public async Task AQueueVerbOnACopyReadBeforeARunStartedTheJobChangesNothing()
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        var verbCopy = (await _repository.GetAsync(_databasePath, job.Id))!;
        job.Status = JobStatus.CrfSearching;
        await _repository.UpdateAsync(_databasePath, job);

        (await _repository.DropAsync(_databasePath, verbCopy)).Should().BeFalse();
        (await _repository.SetHeldAsync(_databasePath, verbCopy, held: true)).Should().BeFalse();
        (await _repository.RunNextAsync(_databasePath, verbCopy)).Should().BeFalse();
        var stored = (await _repository.GetAsync(_databasePath, job.Id))!;
        stored.Status.Should().Be(JobStatus.CrfSearching);
        stored.Held.Should().BeFalse();
        stored.Priority.Should().Be(0);
    }

    /// <summary>
    /// VOPT-112: Rejected is terminal wherever the repository asks. The job leaves the open list and no longer blocks a new job
    /// for its original, and the same compare-and-swap as a drop refuses a copy read before another command changed the row.
    /// </summary>
    [Fact]
    public async Task ARejectedJobIsTerminalAndItsStaleCopyIsRefused()
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        job.Status = JobStatus.Validating;
        await _repository.UpdateAsync(_databasePath, job);
        job.Status = JobStatus.ReadyToFinalize;
        await _repository.UpdateAsync(_databasePath, job);
        var staleCopy = (await _repository.GetAsync(_databasePath, job.Id))!;
        (await _repository.SetHeldAsync(_databasePath, job, held: true)).Should().BeTrue();

        (await _repository.RejectAsync(_databasePath, staleCopy)).Should().BeFalse();
        (await _repository.GetAsync(_databasePath, job.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);

        (await _repository.RejectAsync(_databasePath, job)).Should().BeTrue();

        var stored = (await _repository.GetAsync(_databasePath, job.Id))!;
        (stored.Status, stored.Held, stored.FailureCategory, stored.FailureMessage).Should().Be((JobStatus.Rejected, false, "Rejected", "Rejected by user."));
        stored.CompletedUtc.Should().NotBeNull();
        (await _repository.ListAsync(_databasePath, terminal: true)).Should().ContainSingle(ended => ended.Id == job.Id);
        (await _repository.ListAsync(_databasePath, terminal: false)).Should().BeEmpty();
        (await _repository.FindOpenBySourceAsync(_databasePath, job.SourcePath)).Should().BeNull();
        await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
    }

    /// <summary>
    /// VOPT-115: a dismissal marks failed jobs only, leaves their status and history order alone, and is refused on a stale copy.
    /// A job that leaves Failed loses it, so a later failure of the same job shows again.
    /// </summary>
    [Fact]
    public async Task DismissMarksOnlyFailedJobsAndKeepsTheirStatusAndOrder()
    {
        var older = await FailAsync(@"C:\Videos\a.mp4");
        var newer = await FailAsync(@"C:\Videos\b.mp4");
        var dropped = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued, @"C:\Videos\c.mp4"));
        (await _repository.DropAsync(_databasePath, dropped)).Should().BeTrue();
        var historyOrder = (await _repository.ListAsync(_databasePath, terminal: true)).Select(job => job.Id).ToArray();
        var staleCopy = (await _repository.GetAsync(_databasePath, older.Id))!;

        (await _repository.DismissAsync(_databasePath, older)).Should().BeTrue();
        (await _repository.DismissAsync(_databasePath, staleCopy)).Should().BeFalse();
        (await _repository.DismissAsync(_databasePath, dropped)).Should().BeFalse();
        (await _repository.DismissAllFailedAsync(_databasePath)).Should().Be(1);
        (await _repository.DismissAllFailedAsync(_databasePath)).Should().Be(0);

        var history = await _repository.ListAsync(_databasePath, terminal: true);
        history.Select(job => job.Id).Should().Equal(historyOrder);
        history.Where(job => job.DismissedUtc is not null).Select(job => job.Id).Should().BeEquivalentTo([older.Id, newer.Id]);
        history.Single(job => job.Id == older.Id).Status.Should().Be(JobStatus.Failed);

        // A failed finalisation that resumes leaves Failed, and with it the dismissal.
        var resumed = (await _repository.GetAsync(_databasePath, newer.Id))!;
        resumed.Status = JobStatus.ReadyToFinalize;
        await _repository.UpdateAsync(_databasePath, resumed);
        resumed.DismissedUtc.Should().BeNull();
        (await _repository.GetAsync(_databasePath, newer.Id))!.DismissedUtc.Should().BeNull();
    }

    private async Task<JobRecord> FailAsync(string sourcePath)
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued, sourcePath));
        job.Status = JobStatus.Failed;
        job.CompletedUtc = DateTimeOffset.UtcNow;
        await _repository.UpdateAsync(_databasePath, job);
        return job;
    }

    [Fact]
    public async Task RunNextPutsAJobAheadOfEveryOpenJobAndReleasesIt()
    {
        var first = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued, @"C:\Videos\a.mp4"));
        var second = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued, @"C:\Videos\b.mp4"));
        var third = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued, @"C:\Videos\c.mp4"));
        (await _repository.SetHeldAsync(_databasePath, third, held: true)).Should().BeTrue();

        (await _repository.RunNextAsync(_databasePath, second)).Should().BeTrue();
        (await _repository.RunNextAsync(_databasePath, third)).Should().BeTrue();

        var order = JobRunOrder.Sort(await _repository.ListAsync(_databasePath, terminal: false)).Select(job => job.Id);
        order.Should().Equal(third.Id, second.Id, first.Id);
        (await _repository.GetAsync(_databasePath, third.Id))!.Held.Should().BeFalse();
    }

    [Fact]
    public async Task ADropKeepsTheResumePointSoCleanupStillKnowsAnInterruptedFinalisation()
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Interrupted));
        job.ResumeStatus = JobStatus.Finalizing;
        await _repository.UpdateAsync(_databasePath, job);

        (await _repository.DropAsync(_databasePath, job)).Should().BeTrue();

        var stored = (await _repository.GetAsync(_databasePath, job.Id))!;
        stored.Status.Should().Be(JobStatus.Cancelled);
        stored.ResumeStatus.Should().Be(JobStatus.Finalizing);
    }

    [Theory]
    [InlineData(@"C:\Videos\movie.mp4", null, true)]
    [InlineData(@"c:\videos\MOVIE.mp4", null, true)]
    [InlineData(@"C:\Staging\asset-1\other.mp4", "asset-1", true)]
    [InlineData(@"C:\Videos\other.mp4", null, false)]
    public async Task AnOriginalGetsOneOpenJobHoweverManyCommandsQueueIt(string path, string? assetId, bool refused)
    {
        await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        await _repository.CreateAsync(_databasePath, NewImmichJob("asset-1", JobStatus.Queued));
        var duplicate = NewJob(JobStatus.Queued, path);
        duplicate.ImmichAssetId = assetId;

        var create = async () => await _repository.CreateAsync(_databasePath, duplicate);

        if (refused) await create.Should().ThrowAsync<OpenJobExistsException>();
        else await create.Should().NotThrowAsync();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static JobRecord NewJob(JobStatus status, string sourcePath = "C:\\Videos\\movie.mp4") => new()
    {
        Id = Guid.NewGuid(),
        SourcePath = sourcePath,
        SourceFingerprint = "fingerprint",
        Status = status
    };

    private static JobRecord NewImmichJob(string assetId, JobStatus status) => new()
    {
        Id = Guid.NewGuid(),
        SourcePath = $@"C:\Staging\{assetId}.mp4",
        SourceFingerprint = "fingerprint",
        Status = status,
        SourceKind = JobSourceKind.Immich,
        ImmichAssetId = assetId,
        ImmichChecksum = "q0zr+Ia/8Vh9EfuB4P5uB1j7zN0="
    };
}
