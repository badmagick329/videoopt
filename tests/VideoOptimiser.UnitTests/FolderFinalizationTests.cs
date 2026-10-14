using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Immich;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

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
            ? new FinalizationService(_repository, _manifests, new CancelledFingerprints(), new SafeFileInstaller(), new ImmichFinalizer(null!, _repository, _manifests))
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

    /// <summary>As a Ctrl+C in the old code, or a crash marked interrupted by the next run, left it.</summary>
    private async Task InterruptFinalizationAsync(AppSettings settings, JobRecord job)
    {
        job.Status = JobStatus.Finalizing;
        await _repository.UpdateAsync(settings.Database.Path, job);
        await _repository.MarkActiveJobsInterruptedAsync(settings.Database.Path);
    }

    private sealed class CancelledFingerprints : IFileFingerprintService
    {
        public Task<string> CreateAsync(string path, CancellationToken cancellationToken = default) => throw new OperationCanceledException();
    }

    private sealed class CancelledInstaller : ISafeFileInstaller
    {
        public Task<FileInstallation> InstallAsync(string sourcePath, string outputPath, OriginalSettings original, CancellationToken cancellationToken = default) => throw new OperationCanceledException();
    }

    private FinalizationService CreateFinalizer(ISafeFileInstaller installer) =>
        // A folder job never reaches the Immich finalizer, so it gets no client.
        new(_repository, _manifests, _fingerprints, installer, new ImmichFinalizer(null!, _repository, _manifests));

    private async Task<(AppSettings Settings, JobRecord Job, string Source, string Output)> ReadyJobAsync(bool validationPassed = true)
    {
        Directory.CreateDirectory(Path.Combine(_directory, ".video-optimiser"));
        var source = Path.Combine(_directory, "movie.mp4");
        var output = Path.Combine(_directory, ".video-optimiser", "movie.encoding.mp4");
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
