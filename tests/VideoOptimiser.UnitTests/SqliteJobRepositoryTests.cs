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

    [Fact]
    public async Task MarksOnlyInFlightJobsInterrupted()
    {
        var encoding = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Encoding));
        var ready = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.ReadyToFinalize, "C:\\Videos\\other.mp4"));

        await _repository.MarkActiveJobsInterruptedAsync(_databasePath, includeFinalizing: true);

        (await _repository.GetAsync(_databasePath, encoding.Id))!.Status.Should().Be(JobStatus.Interrupted);
        (await _repository.GetAsync(_databasePath, ready.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    [Fact]
    public async Task RejectsInvalidStateTransitions()
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        job.Status = JobStatus.Completed;

        var action = async () => await _repository.UpdateAsync(_databasePath, job);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(JobStatus.Encoding)]
    public async Task OnlyACrfSearchCanEndSkipped(JobStatus from)
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(from));
        job.Status = JobStatus.Skipped;

        var action = async () => await _repository.UpdateAsync(_databasePath, job);

        await action.Should().ThrowAsync<InvalidOperationException>();
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

    [Fact]
    public async Task StoresAndReplacesImmichSkips()
    {
        var recorded = new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-1", "checksum-a", "rules-a", "codec hevc matches no rule", recorded));
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-2", "checksum-b", "rules-a", "shorter than 10 s", recorded));
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-1", "checksum-c", "rules-b", "bitrate too low", recorded.AddDays(1)));

        var skips = await _repository.ListImmichSkipsAsync(_databasePath);

        skips.Should().HaveCount(2);
        skips["asset-1"].Should().Be(new ImmichSkipRecord("asset-1", "checksum-c", "rules-b", "bitrate too low", recorded.AddDays(1)));
        skips["asset-2"].Reason.Should().Be("shorter than 10 s");
    }

    [Fact]
    public async Task AReplacedImmichSkipTakesTheNewVideoFactsOrNone()
    {
        var recorded = new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);
        var facts = new VideoFacts("h264", 1920, 1080, 22_000_000, 31.5, 86_000_000);
        var newer = new VideoFacts("hevc", 3840, 2160, 40_000_000, 62, 300_000_000);
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-1", "checksum-a", "rules-a", "no rule for h264", recorded, facts));
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-1", "checksum-d", "rules-b", "no rule for hevc", recorded, newer));
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-2", "checksum-b", "rules-a", "no rule for h264", recorded, facts));
        await _repository.PutImmichSkipAsync(_databasePath, new ImmichSkipRecord("asset-2", "checksum-c", "rules-b", "too small or not a listed file type", recorded));

        var skips = await _repository.ListImmichSkipsAsync(_databasePath);

        skips["asset-1"].VideoFacts.Should().Be(newer);
        skips["asset-2"].VideoFacts.Should().BeNull();
    }

    [Theory]
    [InlineData(JobSourceKind.File)]
    [InlineData(JobSourceKind.Immich)]
    public async Task AJobKeepsItsVideoFactsThroughCreateAndUpdate(JobSourceKind kind)
    {
        var job = kind == JobSourceKind.File ? NewJob(JobStatus.Queued) : NewImmichJob("asset-1", JobStatus.Queued);
        job.VideoFacts = new VideoFacts("hevc", 3840, 2160, null, 12.25, 52_428_800);
        await _repository.CreateAsync(_databasePath, job);
        (await _repository.GetAsync(_databasePath, job.Id))!.VideoFacts.Should().Be(job.VideoFacts);

        job.VideoFacts = new VideoFacts("h264", 1920, 1080, 18_000_000, 40, 90_000_000);
        job.Status = JobStatus.CrfSearching;
        await _repository.UpdateAsync(_databasePath, job);

        (await _repository.GetAsync(_databasePath, job.Id))!.VideoFacts.Should().Be(job.VideoFacts);
    }

    [Fact]
    public async Task AJobWithoutVideoFactsReadsBackWithout() =>
        (await _repository.GetAsync(_databasePath, (await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued))).Id))!.VideoFacts.Should().BeNull();

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

    /// <summary>R9: crash recovery is a write too, so a copy read before it cannot undo it.</summary>
    [Fact]
    public async Task AWriteFromACopyReadBeforeCrashRecoveryIsRefused()
    {
        var job = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        job.Status = JobStatus.Encoding;
        await _repository.UpdateAsync(_databasePath, job);
        var staleCopy = (await _repository.GetAsync(_databasePath, job.Id))!;
        await _repository.MarkActiveJobsInterruptedAsync(_databasePath, includeFinalizing: true);

        staleCopy.Status = JobStatus.Validating;
        var write = async () => await _repository.UpdateAsync(_databasePath, staleCopy);

        await write.Should().ThrowAsync<JobChangedException>();
        (await _repository.GetAsync(_databasePath, job.Id))!.Status.Should().Be(JobStatus.Interrupted);
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

    [Fact]
    public async Task AFinishedJobDoesNotBlockANewOneForTheSameOriginal()
    {
        var done = await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));
        (await _repository.DropAsync(_databasePath, done)).Should().BeTrue();

        var again = async () => await _repository.CreateAsync(_databasePath, NewJob(JobStatus.Queued));

        await again.Should().NotThrowAsync();
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
