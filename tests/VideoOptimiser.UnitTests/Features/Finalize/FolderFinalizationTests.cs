using VideoOptimiser.Cli;
using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Features.Finalize;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;

namespace VideoOptimiser.UnitTests.Features.Finalize;

public sealed class FolderFinalizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly OutputManifestStore _manifests = new();
    private readonly FileFingerprintService _fingerprints = new();

    [Fact]
    public async Task FinalizeStillCompletesAndShowsTheWarningWhenTheFileDatesCannotBeCopied()
    {
        var (settings, job, source, _) = await ReadyJobAsync();
        var installer = new SafeFileInstaller((_, _, _) => throw new IOException("locked"));

        var result = await CreateFinalizer(installer).FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        result.Message.Should().StartWith("Original deleted. Warning: ").And.Contain("file dates").And.Contain("locked");
        (await _repository.GetAsync(settings.Database.Path, job.Id))!.Status.Should().Be(JobStatus.Completed);
        (await File.ReadAllTextAsync(source)).Should().Be("av1");
    }

    /// <summary>
    /// VOPT-143: a player holding a file open, as during review, blocks the move. Nothing moved, so the job is ready again and
    /// the next finalise, once the file is closed, completes; it used to fail and leave only `queue retry`. One row per move
    /// the installer makes: the original aside, then the AV1 into place, and keep's single move of the AV1.
    /// </summary>
    [Theory]
    [InlineData(OriginalActions.Delete, true)]
    [InlineData(OriginalActions.Archive, false)]
    [InlineData(OriginalActions.Keep, false)]
    public async Task AFileOpenInAnotherProgramLeavesTheJobReadyToFinaliseAgain(string action, bool originalOpen)
    {
        var (settings, job, source, output) = await ReadyJobAsync();
        settings.Original = new OriginalSettings { Action = action, ArchiveDirectory = "Archive" };
        var open = originalOpen ? source : output;
        FinalizationResult result;
        // FileShare.Read, as a player opens a video: reading it still works, moving it doesn't.
        using (new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await CreateFinalizer(new SafeFileInstaller()).FinalizeAsync(settings.Database.Path, job.Id, settings);
        }

        result.ExitCode.Should().Be(ExitCode.FinalisationFailure);
        result.Message.Should().Be($"The {(originalOpen ? "original" : "AV1")} is open in another program: {open}. Nothing was moved. Close it, then run 'video-optimiser finalize {job.Id:N}' again.");
        (await File.ReadAllTextAsync(source)).Should().Be("original");
        (await File.ReadAllTextAsync(output)).Should().Be("av1");
        var stored = (await _repository.GetAsync(settings.Database.Path, job.Id))!;
        stored.Status.Should().Be(JobStatus.ReadyToFinalize);
        stored.FailureMessage.Should().Be(result.Message);
        (await CreateFinalizer(new SafeFileInstaller()).FinalizeAsync(settings.Database.Path, job.Id, settings)).ExitCode.Should().Be(ExitCode.Success);
    }

    /// <summary>A move refused for another reason than an open file still moved nothing, so the job is ready again too.</summary>
    [Fact]
    public async Task AMoveRefusedWithNothingMovedNamesTheCauseAndLeavesTheJobReady()
    {
        var (settings, job, source, _) = await ReadyJobAsync();
        var refused = new FileInstallBlockedException(source, isOriginal: true, inUse: false, new UnauthorizedAccessException("Access to the path is denied."));

        var result = await CreateFinalizer(new ThrowingInstaller(refused)).FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.Message.Should().Be($"The original could not be moved: Access to the path is denied. Nothing was moved. Fix that, then run 'video-optimiser finalize {job.Id:N}' again.");
        (await _repository.GetAsync(settings.Database.Path, job.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    /// <summary>Without proof that nothing moved, the job fails as before, and cleanup keeps its AV1.</summary>
    [Fact]
    public async Task AnUnexpectedInstallerFailureFailsTheJob()
    {
        var (settings, job, _, _) = await ReadyJobAsync();

        var result = await CreateFinalizer(new ThrowingInstaller(new IOException("disk gone"))).FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.ExitCode.Should().Be(ExitCode.FinalisationFailure);
        result.Message.Should().Be("disk gone");
        var stored = (await _repository.GetAsync(settings.Database.Path, job.Id))!;
        stored.Status.Should().Be(JobStatus.Failed);
        stored.FailureCategory.Should().Be("FinalizationFailed");
    }

    /// <summary>VOPT-143: the AV1 is in place when deleting the original fails, so the job is completed and the warning says where the original is.</summary>
    [Fact]
    public async Task AnOriginalThatCannotBeDeletedLeavesTheJobCompletedWithAWarning()
    {
        var (settings, job, source, _) = await ReadyJobAsync();
        var installer = new SafeFileInstaller((_, _, _) => { }, _ => throw new IOException("in use"));

        var result = await CreateFinalizer(installer).FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        var leftover = Directory.EnumerateFiles(_directory, "*.pending-delete*").Should().ContainSingle().Subject;
        result.Message.Should().Be($"AV1 installed as {source}. Warning: the original could not be deleted (in use), so it is still at {leftover}; delete it once you have checked the AV1");
        (await File.ReadAllTextAsync(leftover)).Should().Be("original");
        (await File.ReadAllTextAsync(source)).Should().Be("av1");
        (await _repository.GetAsync(settings.Database.Path, job.Id))!.Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task FinalizeRefusesAndLeavesTheOriginalUntouchedWhenItChangedAfterEncoding()
    {
        var (settings, job, source, output) = await ReadyJobAsync();
        await File.WriteAllTextAsync(source, "edited after the encode");

        var result = await CreateFinalizer(new SafeFileInstaller()).FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.ExitCode.Should().Be(ExitCode.FinalisationFailure);
        result.Message.Should().Be("Original changed after encoding.");
        (await File.ReadAllTextAsync(source)).Should().Be("edited after the encode");
        File.Exists(output).Should().BeTrue("the AV1 was never installed");
        var stored = (await _repository.GetAsync(settings.Database.Path, job.Id))!;
        stored.Status.Should().Be(JobStatus.Failed);
        stored.FailureCategory.Should().Be("FinalizationFailed");
    }

    [Fact]
    public async Task FinalizeRefusesAndLeavesTheOriginalUntouchedWhenValidationDidNotPass()
    {
        var (settings, job, source, output) = await ReadyJobAsync(validationPassed: false);

        var result = await CreateFinalizer(new SafeFileInstaller()).FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.ExitCode.Should().Be(ExitCode.ValidationFailure);
        result.Message.Should().Be("Job validation did not pass.");
        (await File.ReadAllTextAsync(source)).Should().Be("original");
        File.Exists(output).Should().BeTrue("the AV1 was never installed");
        (await _repository.GetAsync(settings.Database.Path, job.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    /// <summary>
    /// Ctrl+C while the original is read, or at the installer's first line, comes before any file moves, so the job is simply
    /// ready again: no category, nothing to review (VOPT-70).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CtrlCBeforeAnyFileMovesLeavesTheJobReady(bool whileReadingTheOriginal)
    {
        var (settings, job, source, output) = await ReadyJobAsync();
        var finalizer = whileReadingTheOriginal
            ? new FinalizationService(_repository, _manifests, new CancelledFingerprints(), new SafeFileInstaller(), new ImmichFinalizer(null!, _repository, _manifests, new CommandHints(null)), new CommandHints(null))
            : CreateFinalizer(new CancelledInstaller());

        var finalizing = () => finalizer.FinalizeAsync(settings.Database.Path, job.Id, settings);

        await finalizing.Should().ThrowAsync<OperationCanceledException>();
        var stored = (await _repository.GetAsync(settings.Database.Path, job.Id))!;
        stored.Status.Should().Be(JobStatus.ReadyToFinalize);
        stored.FailureCategory.Should().BeNull();
        (await File.ReadAllTextAsync(source)).Should().Be("original");
        File.Exists(output).Should().BeTrue();
    }

    [Fact]
    public async Task AnInterruptedFinalisationWhoseFilesNeverMovedFinalises()
    {
        var (settings, job, source, _) = await ReadyJobAsync();
        await InterruptFinalizationAsync(settings, job);

        var result = await CreateFinalizer(new SafeFileInstaller()).FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        (await _repository.GetAsync(settings.Database.Path, job.Id))!.Status.Should().Be(JobStatus.Completed);
        (await File.ReadAllTextAsync(source)).Should().Be("av1");
    }

    /// <summary>Something moved, so only a person can tell which file is which; cleanup must keep the AV1, maybe the only copy.</summary>
    [Fact]
    public async Task AnInterruptedFinalisationWhoseOriginalMovedNeedsReviewAndCleanupKeepsTheAv1()
    {
        var (settings, job, source, output) = await ReadyJobAsync();
        await InterruptFinalizationAsync(settings, job);
        File.Move(source, Path.Combine(_directory, "movie.pending-delete.mp4"));

        var result = await CreateFinalizer(new SafeFileInstaller()).FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.ExitCode.Should().Be(ExitCode.FinalisationFailure);
        result.Message.Should().Be("Finalisation was interrupted and requires manual review.");
        var stored = (await _repository.GetAsync(settings.Database.Path, job.Id))!;
        stored.Status.Should().Be(JobStatus.Failed);
        stored.FailureCategory.Should().Be("ManualInterventionRequired");
        TemporaryOutputClassifier.AbandonedReason(stored, stored.Attempt).Should().BeNull();
        File.Exists(output).Should().BeTrue();
    }

    /// <summary>
    /// VOPT-85: before VOPT-70 a Ctrl+C even before any file moved left the job failed for review. Its files are as they were
    /// and the AV1 is validated, so `finalize` takes it back instead of leaving only `queue retry`, which encodes again.
    /// </summary>
    [Fact]
    public async Task AFailedFinalisationWhoseFilesNeverMovedFinalises()
    {
        var (settings, job, source, _) = await ReadyJobAsync();
        await FailAsAnOlderVersionDidAsync(settings, job);

        var result = await CreateFinalizer(new SafeFileInstaller()).FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        var stored = (await _repository.GetAsync(settings.Database.Path, job.Id))!;
        stored.Status.Should().Be(JobStatus.Completed);
        stored.FailureCategory.Should().BeNull();
        stored.FailureMessage.Should().BeNull();
        stored.ResumeStatus.Should().BeNull();
        (await File.ReadAllTextAsync(source)).Should().Be("av1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailedFinalisationWhoseFilesMovedStaysFailedAndCleanupKeepsTheAv1(bool originalEdited)
    {
        var (settings, job, source, output) = await ReadyJobAsync();
        await FailAsAnOlderVersionDidAsync(settings, job);
        var failedUtc = (await _repository.GetAsync(settings.Database.Path, job.Id))!.CompletedUtc;
        if (originalEdited) await File.WriteAllTextAsync(source, "edited since");
        else File.Move(source, Path.Combine(_directory, "movie.pending-delete.mp4"));

        var result = await CreateFinalizer(new SafeFileInstaller()).FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.ExitCode.Should().Be(ExitCode.FinalisationFailure);
        result.Message.Should().Be("Finalisation was interrupted and requires manual review.");
        var stored = (await _repository.GetAsync(settings.Database.Path, job.Id))!;
        stored.Status.Should().Be(JobStatus.Failed);
        stored.FailureCategory.Should().Be("ManualInterventionRequired");
        stored.CompletedUtc.Should().Be(failedUtc, "a check that changed nothing leaves the record as it was");
        TemporaryOutputClassifier.AbandonedReason(stored, stored.Attempt).Should().BeNull();
        File.Exists(output).Should().BeTrue();
    }

    /// <summary>Without the validation record nothing proves the files never moved, so it is reviewed, not converted again.</summary>
    [Fact]
    public async Task AnInterruptedFinalisationWithoutItsValidationRecordNeedsReview()
    {
        var (settings, job, _, output) = await ReadyJobAsync();
        await InterruptFinalizationAsync(settings, job);
        File.Delete(_manifests.GetPath(output));

        var result = await CreateFinalizer(new SafeFileInstaller()).FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.Message.Should().Be("Finalisation was interrupted and requires manual review.");
        var stored = (await _repository.GetAsync(settings.Database.Path, job.Id))!;
        stored.FailureCategory.Should().Be("ManualInterventionRequired");
        TemporaryOutputClassifier.AbandonedReason(stored, stored.Attempt).Should().BeNull();
    }

    /// <summary>F4 (VOPT-60): the job stays ready, since the AV1 may now be the only copy and cancelling it lets cleanup delete it.</summary>
    [Fact]
    public async Task AMissingOriginalStopsOnlyThisJobWhichStaysReady()
    {
        var (settings, job, source, output) = await ReadyJobAsync();
        File.Delete(source);

        var result = await CreateFinalizer(new SafeFileInstaller()).FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.ExitCode.Should().Be(ExitCode.FinalisationFailure);
        result.Message.Should().Be($"Original does not exist: {source}. If you moved it, put it back and run 'video-optimiser finalize {job.Id:N}' again. If you deleted it on purpose, the AV1 at {output} is now the only copy: move it somewhere safe before running 'video-optimiser queue drop {job.Id:N}'.");
        (await _repository.GetAsync(settings.Database.Path, job.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
        File.Exists(output).Should().BeTrue();
    }

    [Fact]
    public async Task AMissingAv1StopsOnlyThisJobWhichStaysReady()
    {
        var (settings, job, _, output) = await ReadyJobAsync();
        File.Delete(output);

        var result = await CreateFinalizer(new SafeFileInstaller()).FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.Message.Should().Be($"The AV1 is missing: {output}. If you moved it, put it back and run 'video-optimiser finalize {job.Id:N}' again. To convert the video again instead, run 'video-optimiser queue drop {job.Id:N}', then 'video-optimiser queue retry {job.Id:N}'.");
        (await _repository.GetAsync(settings.Database.Path, job.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    /// <summary>
    /// F2: a missing validation record used to end the whole batch with exit 3 and no summary. Now it is this job's result, so
    /// the batch goes on to the rest (the batch itself is in <c>FinalizeCommandsTests</c>).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMissingOrUnreadableValidationRecordStopsOnlyItsJob(bool deleted)
    {
        var (settings, broken, _, brokenOutput) = await ReadyJobAsync();
        var manifestPath = _manifests.GetPath(brokenOutput);
        if (deleted) File.Delete(manifestPath);
        else await File.WriteAllTextAsync(manifestPath, "{ not json");

        var result = await CreateFinalizer(new SafeFileInstaller()).FinalizeAsync(settings.Database.Path, broken.Id, settings);

        result.ExitCode.Should().Be(ExitCode.FinalisationFailure);
        result.Message.Should().Be($"The AV1's validation record is missing or unreadable: {manifestPath}, so the AV1 can't be checked. To convert the video again, run 'video-optimiser queue drop {broken.Id:N}', then 'video-optimiser queue retry {broken.Id:N}'.");
        (await _repository.GetAsync(settings.Database.Path, broken.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    /// <summary>As a Ctrl+C in the old code, or a crash marked interrupted by the next run, left it.</summary>
    private async Task InterruptFinalizationAsync(AppSettings settings, JobRecord job)
    {
        job.Status = JobStatus.Finalizing;
        await _repository.UpdateAsync(settings.Database.Path, job);
        await _repository.MarkActiveJobsInterruptedAsync(settings.Database.Path, includeFinalizing: true);
    }

    /// <summary>The state a Ctrl+C during `finalize` left before VOPT-70: failed for review, nothing moved.</summary>
    private async Task FailAsAnOlderVersionDidAsync(AppSettings settings, JobRecord job)
    {
        await InterruptFinalizationAsync(settings, job);
        job = (await _repository.GetAsync(settings.Database.Path, job.Id))!;
        job.Status = JobStatus.Failed;
        job.FailureCategory = FailureCategories.ManualInterventionRequired;
        job.FailureMessage = FinalizationRules.ManualReviewMessage;
        job.CompletedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        await _repository.UpdateAsync(settings.Database.Path, job);
    }

    private sealed class CancelledFingerprints : IFileFingerprintService
    {
        public Task<string> CreateAsync(string path, CancellationToken cancellationToken = default) => throw new OperationCanceledException();
    }

    private sealed class CancelledInstaller : ISafeFileInstaller
    {
        public Task<FileInstallation> InstallAsync(string sourcePath, string outputPath, OriginalSettings original, CancellationToken cancellationToken = default) => throw new OperationCanceledException();
    }

    private sealed class ThrowingInstaller(Exception exception) : ISafeFileInstaller
    {
        public Task<FileInstallation> InstallAsync(string sourcePath, string outputPath, OriginalSettings original, CancellationToken cancellationToken = default) => throw exception;
    }

    private FinalizationService CreateFinalizer(ISafeFileInstaller installer) =>
        // A folder job never reaches the Immich finalizer, so it gets no client.
        new(_repository, _manifests, _fingerprints, installer, new ImmichFinalizer(null!, _repository, _manifests, new CommandHints(null)), new CommandHints(null));

    private async Task<(AppSettings Settings, JobRecord Job, string Source, string Output)> ReadyJobAsync(string name = "movie.mp4", bool validationPassed = true)
    {
        Directory.CreateDirectory(Path.Combine(_directory, ".video-optimiser"));
        var source = Path.Combine(_directory, name);
        var output = Path.Combine(_directory, ".video-optimiser", $"{Path.GetFileNameWithoutExtension(name)}.encoding.mp4");
        await File.WriteAllTextAsync(source, "original");
        await File.WriteAllTextAsync(output, "av1");
        var fingerprint = await _fingerprints.CreateAsync(source);
        await _manifests.SaveAsync(new OutputManifest
        {
            SourcePath = source,
            SourceFingerprint = fingerprint,
            OutputPath = output,
            Crf = 30,
            CreatedUtc = DateTimeOffset.UtcNow,
            Validation = new ValidationReport { Passed = validationPassed }
        });
        var settings = new AppSettings { Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") } };
        var job = await _repository.CreateAsync(settings.Database.Path, new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = source,
            SourceFingerprint = fingerprint,
            Status = JobStatus.ReadyToFinalize,
            Attempt = 1,
            OutputPath = output
        });
        return (settings, job, source, output);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
